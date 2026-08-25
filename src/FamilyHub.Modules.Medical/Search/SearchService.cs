using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.MedicalRecords;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.Search;

/// <summary>
/// Единая точка входа поиска (этап 3, ADR-0003) — фасад над пятью источниками с РАЗДЕЛЬНЫМ
/// контролем доступа (ни один источник не может отдать данные вне scope пользователя):
///   1. Лекарства — Postgres-FTS (tsvector+pg_trgm), скоуп: семьи, где пользователь активный член;
///   2. Справочник kb.global_medications_kb — Postgres-FTS, обезличен и глобален по определению;
///   3. Анализы и 4. Врачи — обе in-memory (MedicalRecordService.SearchAsync, единая таблица
///      MedicalRecord с дискриминатором Kind), скоуп: владелец + расшаренные, разделяются фильтром
///      по Kind (см. SearchMedicalRecordsAsync).
///   5. Дни рождения — in-memory (IBirthdaySearchSource, Modules.Birthdays через DI-абстракцию
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
    /// (общий поиск из шапки). Экономия не косметическая: анализы/врачи — самый дорогой источник,
    /// он расшифровывает ВСЕ видимые пользователю медкарты нужного вида (см.
    /// MedicalRecordService.SearchAsync); не запрошенный источник не трогает БД вовсе.
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
        var records = await SearchMedicalRecordsAsync(userId, q, wantsAll, types, ct);
        var birthdayHits = wantsAll || types!.Contains(SearchResultType.Birthday)
            ? await birthdays.SearchAsync(userId, q, PerSourceLimit, ct)
            : [];
        var indicators = wantsAll || types!.Contains(SearchResultType.Indicator)
            ? await SearchIndicatorsAsync(userId, q, ct)
            : [];

        var items = medications
            .Concat(kb)
            .Concat(records)
            .Concat(birthdayHits.Select(ToSearchItem))
            .Concat(indicators)
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

    /// <summary>Справочник обезличен и глобален по определению (задача 2.6) — доступен любому вошедшему с согласием.
    /// "{q} = ANY(Aliases)" (этап 4) — точное совпадение по торговому названию (напр. "нурофен" находит запись
    /// "ибупрофен"); Aliases не входит в search_vector (Postgres: array_to_string не IMMUTABLE, не годится для
    /// generated-колонки, см. миграцию AddMedicationEnrichment) — поэтому проверяется отдельным условием.</summary>
    private async Task<List<SearchResultItem>> SearchKbAsync(string q, CancellationToken ct)
    {
        // Aliases хранятся уже нормализованными (lowercase, см. KbWriter/MedicationNameNormalizer) —
        // сравниваем lower(q), иначе "Нурофен" (как ввёл пользователь) не совпал бы с "нурофен".
        var rows = await db.Database.SqlQuery<KbSearchRow>($"""
            SELECT "Id", "DisplayName",
                   GREATEST(
                       ts_rank(search_vector, plainto_tsquery('russian', {q})),
                       similarity("DisplayName", {q}),
                       CASE WHEN lower({q}) = ANY("Aliases") THEN 1.0 ELSE 0.0 END
                   ) AS "Score"
            FROM kb.global_medications_kb
            WHERE search_vector @@ plainto_tsquery('russian', {q})
               OR similarity("DisplayName", {q}) > 0.3
               OR lower({q}) = ANY("Aliases")
            ORDER BY "Score" DESC
            LIMIT {PerSourceLimit}
            """).ToListAsync(ct);

        return rows.Select(r => new SearchResultItem(SearchResultType.Kb, r.Id, r.DisplayName, null, r.Score)).ToList();
    }

    /// <summary>
    /// Анализы и посещения врачей — одна и та же таблица (MedicalRecord.Kind), поэтому либо один
    /// запрос на оба вида (types содержит и Record, и Visit, либо запрошены все источники), либо
    /// один запрос на конкретный вид. Два отдельных вызова при wantsAll удвоили бы самый дорогой
    /// источник — расшифровку всех видимых пользователю медкарт (см. MedicalRecordService.SearchAsync).
    /// </summary>
    private async Task<List<SearchResultItem>> SearchMedicalRecordsAsync(
        Guid userId, string q, bool wantsAll, IReadOnlySet<SearchResultType>? types, CancellationToken ct)
    {
        var wantsRecord = wantsAll || types!.Contains(SearchResultType.Record);
        var wantsVisit = wantsAll || types!.Contains(SearchResultType.Visit);
        if (!wantsRecord && !wantsVisit) return [];

        MedicalRecordKind? kind = (wantsRecord, wantsVisit) switch
        {
            (true, true) => null,
            (true, false) => MedicalRecordKind.Analysis,
            _ => MedicalRecordKind.DoctorVisit,
        };

        var hits = await medicalRecords.SearchAsync(userId, q, kind, PerSourceLimit, ct);
        return hits.Select(ToSearchItem).ToList();
    }

    private static SearchResultItem ToSearchItem(MedicalRecordSearchHit hit)
    {
        var record = hit.Record;
        if (record.Kind == MedicalRecordKind.DoctorVisit)
        {
            var visitTitle = string.IsNullOrWhiteSpace(record.Doctor)
                ? $"{record.PersonName} — {record.RecordDate:dd.MM.yyyy}"
                : $"{record.PersonName} · {record.Doctor}";
            var visitSnippet = Truncate(record.Description);
            return new SearchResultItem(SearchResultType.Visit, record.Id, visitTitle, visitSnippet, hit.Score);
        }

        var title = $"{record.PersonName} — {record.RecordDate:dd.MM.yyyy}";
        var snippet = Truncate(record.Description ?? record.Doctor);
        return new SearchResultItem(SearchResultType.Record, record.Id, title, snippet, hit.Score);
    }

    /// <summary>Показатели анализов (ветка medicalrecords) — AnalyteKey/Flag plaintext,
    /// триграммный поиск прямо в SQL (как у медикаментов), но scope доступа берётся из того же
    /// предиката видимости, что и у самих записей (MedicalRecordService.GetVisibleRecordIdsAsync),
    /// не собственной копии. Один результат на показатель (DISTINCT ON), самая свежая запись —
    /// у показателя может быть много точек в истории, но найти его в поиске нужно один раз.
    /// Значение в сниппет НЕ попадает — только факт отклонения (тот же принцип экономии, что и у
    /// остальных источников: не расшифровывать/не раскрывать больше, чем нужно для навигации).</summary>
    private async Task<List<SearchResultItem>> SearchIndicatorsAsync(Guid userId, string q, CancellationToken ct)
    {
        var recordIds = await medicalRecords.GetVisibleRecordIdsAsync(userId, MedicalRecordKind.Analysis, ct);
        if (recordIds.Count == 0) return [];

        var rows = await db.Database.SqlQuery<IndicatorSearchRow>($"""
            SELECT DISTINCT ON ("AnalyteKey")
                   "Id", "AnalyteKey", "DisplayName", "Flag", "RecordDate",
                   similarity("AnalyteKey", {q}) AS "Score"
            FROM medical."LabIndicators"
            WHERE "MedicalRecordId" = ANY({recordIds.ToArray()})
              AND similarity("AnalyteKey", {q}) > 0.3
            ORDER BY "AnalyteKey", "RecordDate" DESC
            LIMIT {PerSourceLimit}
            """).ToListAsync(ct);

        return rows
            .OrderByDescending(r => r.Score)
            .Select(r => new SearchResultItem(
                SearchResultType.Indicator, r.Id, r.DisplayName,
                $"{FlagText((IndicatorFlag)r.Flag)} · {r.RecordDate:dd.MM.yyyy}", r.Score))
            .ToList();
    }

    private static string FlagText(IndicatorFlag flag) => flag switch
    {
        IndicatorFlag.Low => "ниже нормы",
        IndicatorFlag.High => "выше нормы",
        IndicatorFlag.Critical => "критическое отклонение",
        IndicatorFlag.Normal => "в норме",
        _ => "норма неизвестна",
    };

    private static string? Truncate(string? snippet) =>
        snippet is { Length: > 160 } ? snippet[..160] + "…" : snippet;

    private static SearchResultItem ToSearchItem(BirthdaySearchHit hit) =>
        new(SearchResultType.Birthday, hit.Id, hit.PersonName, null, hit.Score,
            Birthday: new BirthdayContext(hit.FamilyId, hit.FamilyName, hit.Date));
}
