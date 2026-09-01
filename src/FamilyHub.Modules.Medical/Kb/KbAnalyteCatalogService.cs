using System.Text.Json;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Kb;

/// <summary>
/// Чтение справочника показателей для UI (редизайн v2, /health/kb/indicators + панель справки) —
/// зеркало KbCatalogService (медикаменты) на другую таблицу. Raw SQL по тем же причинам:
/// search_vector/Aliases вне EF-модели (см. GlobalLabAnalyteKbConfiguration), уже есть индексы
/// (search_vector GIN, DisplayName gin_trgm_ops, Aliases GIN) — миграция
/// 20260825090300_AddMedicalDocumentExtraction их создаёт, здесь всё готово к чтению.
/// </summary>
public class KbAnalyteCatalogService(AppDbContext db, ILogger<KbAnalyteCatalogService> logger)
{
    private const int DefaultTake = 20;
    private const int MaxTake = 50;

    public async Task<KbAnalyteListResponse> SearchAsync(string? q, int skip, int take, CancellationToken ct = default)
    {
        take = take <= 0 ? DefaultTake : Math.Min(take, MaxTake);
        skip = Math.Max(skip, 0);

        var rows = string.IsNullOrWhiteSpace(q)
            ? await db.Database.SqlQuery<KbAnalyteCatalogRow>($"""
                SELECT "Id", "DisplayName", "Specimen", "PayloadJson"
                FROM kb.global_lab_analytes_kb
                ORDER BY "DisplayName"
                OFFSET {skip} LIMIT {take}
                """).ToListAsync(ct)
            : await db.Database.SqlQuery<KbAnalyteCatalogRow>($"""
                SELECT "Id", "DisplayName", "Specimen", "PayloadJson"
                FROM kb.global_lab_analytes_kb
                WHERE search_vector @@ plainto_tsquery('russian', {q})
                   OR similarity("DisplayName", {q}) > 0.3
                   OR lower({q}) = ANY("Aliases")
                ORDER BY GREATEST(
                    ts_rank(search_vector, plainto_tsquery('russian', {q})),
                    similarity("DisplayName", {q})
                ) DESC
                OFFSET {skip} LIMIT {take}
                """).ToListAsync(ct);

        var items = rows.Select(r =>
            new KbAnalyteListItem(r.Id, r.DisplayName, r.Specimen, ParsePayload(r.Id, r.PayloadJson).PlainExplanation)).ToList();
        return new KbAnalyteListResponse(items, HasMore: rows.Count == take);
    }

    public async Task<KbAnalyteCard?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQuery<KbAnalyteDetailRow>($"""
            SELECT "Id", "DisplayName", "Specimen", "PayloadJson", "Source", "UpdatedAt"
            FROM kb.global_lab_analytes_kb
            WHERE "Id" = {id}
            """).FirstOrDefaultAsync(ct);

        return row is null ? null : await BuildCardAsync(row, ct);
    }

    /// <summary>Резолв «Что смотрят вместе» — живым поиском по NormalizedName (не хранимые
    /// ссылки, см. LabAnalyteSummary.RelatedAnalytes). Один запрос на всю карточку, не N+1 на
    /// каждое имя. Ненайденное имя — чип без ссылки (Id=null), не ошибка: обогащение может
    /// заполнить статью позже, чем эта.</summary>
    private async Task<KbAnalyteCard> BuildCardAsync(KbAnalyteDetailRow row, CancellationToken ct)
    {
        var payload = ParsePayload(row.Id, row.PayloadJson);
        var refRanges = LabAnalyteKbPayload.ParseRefRanges(row.PayloadJson)
            .Select(r => new KbRefRangeDto(
                r.AgeFrom, r.AgeTo, r.Sex, r.Low, r.High, r.Unit,
                r.NormKind, r.Population, r.PopulationDetail, r.SourceDomain))
            .ToList();

        var relatedNames = LabAnalyteKbPayload.ParseRelatedNames(row.PayloadJson);
        var related = await ResolveRelatedAsync(relatedNames, ct);

        return new KbAnalyteCard(
            row.Id, row.DisplayName, row.Specimen, payload.LoincCode, payload.DefaultUnit, payload.PlainExplanation,
            payload.WhyMeasured, payload.HighMeans, payload.LowMeans, refRanges, related, row.Source, row.UpdatedAt);
    }

    private async Task<List<KbRelatedAnalyte>> ResolveRelatedAsync(List<string> displayNames, CancellationToken ct)
    {
        if (displayNames.Count == 0) return [];

        var normalized = displayNames.Select(LabAnalyteNormalizer.Normalize).Where(n => n.Length > 0).Distinct().ToArray();
        if (normalized.Length == 0) return [];

        var matches = await db.Database.SqlQuery<KbRelatedMatchRow>($"""
            SELECT "Id", "DisplayName", "NormalizedName"
            FROM kb.global_lab_analytes_kb
            WHERE "NormalizedName" = ANY({normalized})
            """).ToListAsync(ct);
        var byNormalized = matches.ToDictionary(m => m.NormalizedName, m => m);

        return displayNames.Select(name =>
        {
            var key = LabAnalyteNormalizer.Normalize(name);
            return byNormalized.TryGetValue(key, out var match)
                ? new KbRelatedAnalyte(match.Id, match.DisplayName)
                : new KbRelatedAnalyte(null, name);
        }).ToList();
    }

    /// <summary>Malformed JSON не должен ронять чтение справочника — вернуть пустой payload и залогировать
    /// (тот же приём, что KbCatalogService.ParsePayload).</summary>
    private KbAnalytePayloadDto ParsePayload(Guid id, string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<KbAnalytePayloadDto>(payloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? new KbAnalytePayloadDto(null, null, null, null, null, null);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Не удалось распарсить PayloadJson показателя справочника {KbId}", id);
            return new KbAnalytePayloadDto(null, null, null, null, null, null);
        }
    }
}
