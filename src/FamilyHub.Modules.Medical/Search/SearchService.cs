using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.MedicalRecords;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.Search;

/// <summary>
/// Единая точка входа поиска (этап 3, ADR-0003) — фасад над четырьмя источниками с РАЗДЕЛЬНЫМ
/// контролем доступа (ни один источник не может отдать данные вне scope пользователя):
///   1. Лекарства — Postgres-FTS (tsvector+pg_trgm), скоуп: семьи, где пользователь активный член;
///   2. Справочник kb.global_medications_kb — Postgres-FTS, обезличен и глобален по определению;
///   3. Медкарты — in-memory (MedicalRecordService.SearchAsync), скоуп: владелец + расшаренные.
///   4. Дни рождения — in-memory (IBirthdaySearchSource, Modules.Birthdays через DI-абстракцию
///      из Infrastructure — этот модуль не ссылается на Modules.Birthdays напрямую), скоуп: семьи,
///      где пользователь активный член (как у лекарств — семейный ресурс, не персональный).
/// </summary>
public class SearchService(
    AppDbContext db, IFamilyAccessService access, MedicalRecordService medicalRecords, IBirthdaySearchSource birthdays)
{
    private const int MinQueryLength = 2;
    private const int PerSourceLimit = 20;

    /// <param name="types">
    /// Ограничить источники (например, только «Лекарства»). <c>null</c>/пустой набор — все
    /// (общий поиск из шапки). Экономия не косметическая: <c>record</c> — самый дорогой источник,
    /// он расшифровывает ВСЕ видимые пользователю медкарты (см. MedicalRecordService.SearchAsync);
    /// не запрошенный источник не трогает БД вовсе.
    /// </param>
    public async Task<SearchResponse> SearchAsync(
        Guid userId, string? query, IReadOnlySet<SearchResultType>? types = null, CancellationToken ct = default)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q) || q.Length < MinQueryLength)
            return new SearchResponse([]);

        var wantsAll = types is null || types.Count == 0;

        // Последовательно, НЕ Task.WhenAll: все источники читают через один и тот же scoped
        // AppDbContext (DbContext не потокобезопасен для параллельных операций).
        var medications = wantsAll || types!.Contains(SearchResultType.Medication)
            ? await SearchMedicationsAsync(userId, q, ct)
            : [];
        var kb = wantsAll || types!.Contains(SearchResultType.Kb)
            ? await SearchKbAsync(q, ct)
            : [];
        var records = wantsAll || types!.Contains(SearchResultType.Record)
            ? await medicalRecords.SearchAsync(userId, q, PerSourceLimit, ct)
            : [];
        var birthdayHits = wantsAll || types!.Contains(SearchResultType.Birthday)
            ? await birthdays.SearchAsync(userId, q, PerSourceLimit, ct)
            : [];

        var items = medications
            .Concat(kb)
            .Concat(records.Select(ToSearchItem))
            .Concat(birthdayHits.Select(ToSearchItem))
            .OrderByDescending(i => i.Score)
            .ToList();

        return new SearchResponse(items);
    }

    /// <summary>Скоуп — только семьи, где пользователь активный член (инвариант 1: списки фильтруются по FamilyId).</summary>
    private async Task<List<SearchResultItem>> SearchMedicationsAsync(Guid userId, string q, CancellationToken ct)
    {
        var familyIds = await access.GetActiveFamilyIdsAsync(userId, ct);
        if (familyIds.Count == 0) return [];

        // Джойны только обогащают уже отфильтрованные по FamilyId строки контекстом (где лежит,
        // до какого срока годно) — сам скоуп доступа не меняется (WHERE ANY(familyIds) как и раньше).
        var rows = await db.Database.SqlQuery<MedicationSearchRow>($"""
            SELECT m."Id", m."Name", m."ExpiryDate",
                   m."MedkitId", k."Name" AS "MedkitName",
                   m."FamilyId", f."Name" AS "FamilyName",
                   GREATEST(
                       ts_rank(m.search_vector, plainto_tsquery('russian', {q})),
                       similarity(m."Name", {q})
                   ) AS "Score"
            FROM medical."Medications" m
            JOIN medical."Medkits"   k ON k."Id" = m."MedkitId"
            JOIN identity."Families" f ON f."Id" = m."FamilyId"
            WHERE m."FamilyId" = ANY({familyIds.ToArray()})
              AND (m.search_vector @@ plainto_tsquery('russian', {q}) OR similarity(m."Name", {q}) > 0.3)
            ORDER BY "Score" DESC
            LIMIT {PerSourceLimit}
            """).ToListAsync(ct);

        return rows.Select(r => new SearchResultItem(
            SearchResultType.Medication, r.Id, r.Name, null, r.Score,
            new MedicationContext(r.FamilyId, r.FamilyName, r.MedkitId, r.MedkitName, r.ExpiryDate))).ToList();
    }

    /// <summary>Справочник обезличен и глобален по определению (задача 2.6) — доступен любому вошедшему с согласием.</summary>
    private async Task<List<SearchResultItem>> SearchKbAsync(string q, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<KbSearchRow>($"""
            SELECT "Id", "DisplayName",
                   GREATEST(
                       ts_rank(search_vector, plainto_tsquery('russian', {q})),
                       similarity("DisplayName", {q})
                   ) AS "Score"
            FROM kb.global_medications_kb
            WHERE search_vector @@ plainto_tsquery('russian', {q}) OR similarity("DisplayName", {q}) > 0.3
            ORDER BY "Score" DESC
            LIMIT {PerSourceLimit}
            """).ToListAsync(ct);

        return rows.Select(r => new SearchResultItem(SearchResultType.Kb, r.Id, r.DisplayName, null, r.Score)).ToList();
    }

    private static SearchResultItem ToSearchItem(MedicalRecordSearchHit hit)
    {
        var record = hit.Record;
        var title = $"{record.PersonName} — {record.RecordDate:dd.MM.yyyy}";
        var snippet = record.Description ?? record.Doctor;
        if (snippet is { Length: > 160 }) snippet = snippet[..160] + "…";
        return new SearchResultItem(SearchResultType.Record, record.Id, title, snippet, hit.Score);
    }

    private static SearchResultItem ToSearchItem(BirthdaySearchHit hit) =>
        new(SearchResultType.Birthday, hit.Id, hit.PersonName, null, hit.Score,
            Birthday: new BirthdayContext(hit.FamilyId, hit.FamilyName, hit.Date));
}
