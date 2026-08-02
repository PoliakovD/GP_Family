using System.Text.Json;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Kb;

/// <summary>
/// Чтение справочника для UI (раздел «Справочник», карточка препарата) — в отличие от
/// KbLookupService (внутренний каскад для конвейера обогащения: один нормализованный запрос →
/// лучшее совпадение), здесь произвольный поисковый запрос с пагинацией и полная карточка по Id.
/// Raw SQL — как и весь остальной доступ к kb.global_medications_kb (search_vector/Aliases вне EF-модели).
/// </summary>
public class KbCatalogService(AppDbContext db, ILogger<KbCatalogService> logger)
{
    private const int DefaultTake = 20;
    private const int MaxTake = 50;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<KbListResponse> SearchAsync(string? q, int skip, int take, CancellationToken ct = default)
    {
        take = take <= 0 ? DefaultTake : Math.Min(take, MaxTake);
        skip = Math.Max(skip, 0);

        var rows = string.IsNullOrWhiteSpace(q)
            ? await db.Database.SqlQuery<KbCatalogRow>($"""
                SELECT "Id", "DisplayName", "PayloadJson"
                FROM kb.global_medications_kb
                ORDER BY "DisplayName"
                OFFSET {skip} LIMIT {take}
                """).ToListAsync(ct)
            // Aliases хранятся уже нормализованными (lowercase) — сравниваем lower(q).
            : await db.Database.SqlQuery<KbCatalogRow>($"""
                SELECT "Id", "DisplayName", "PayloadJson"
                FROM kb.global_medications_kb
                WHERE search_vector @@ plainto_tsquery('russian', {q})
                   OR similarity("DisplayName", {q}) > 0.3
                   OR lower({q}) = ANY("Aliases")
                ORDER BY GREATEST(
                    ts_rank(search_vector, plainto_tsquery('russian', {q})),
                    similarity("DisplayName", {q})
                ) DESC
                OFFSET {skip} LIMIT {take}
                """).ToListAsync(ct);

        var items = rows.Select(r => new KbListItem(r.Id, r.DisplayName, ParsePayload(r.Id, r.PayloadJson).Purpose)).ToList();
        return new KbListResponse(items, HasMore: rows.Count == take);
    }

    public async Task<KbMedicationCard?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQuery<KbDetailRow>($"""
            SELECT "Id", "DisplayName", "PayloadJson", "Source", "UpdatedAt"
            FROM kb.global_medications_kb
            WHERE "Id" = {id}
            """).FirstOrDefaultAsync(ct);

        if (row is null) return null;

        var payload = ParsePayload(row.Id, row.PayloadJson);
        return new KbMedicationCard(
            row.Id, row.DisplayName, payload.InternationalName, payload.TradeNames ?? [],
            payload.Form, payload.Purpose, payload.SimplePurpose, payload.Usage, payload.Storage, payload.Driving, payload.SpecialNotes,
            row.Source, row.UpdatedAt);
    }

    /// <summary>Malformed JSON не должен ронять чтение справочника — вернуть пустой payload и залогировать.
    /// Usage отсутствует у строк со старым PayloadVersion=1 — читается как null, не ошибка.</summary>
    private KbPayloadDto ParsePayload(Guid id, string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<KbPayloadDto>(payloadJson, JsonOptions)
                ?? new KbPayloadDto(null, null, null, null, null, null, null, null, null, null);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Не удалось распарсить PayloadJson записи справочника {KbId}", id);
            return new KbPayloadDto(null, null, null, null, null, null, null, null, null, null);
        }
    }
}
