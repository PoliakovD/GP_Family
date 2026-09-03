using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>Результаты последнего платного поиска по (названию, биоматериалу) + метаданные
/// свежести — зеркало MedicationEnrichment.Enrichment.CachedSearch.</summary>
public record CachedAnalyteSearch(
    IReadOnlyList<WebSnippet> Snippets, string Provider, DateTime LastUpdatedAt, DateTime CanBeUpdatedAfter,
    IReadOnlyDictionary<string, bool>? Overrides = null)
{
    public bool IsFresh => CanBeUpdatedAfter > DateTime.UtcNow;
}

/// <summary>
/// Настоящий кэш обращений к платному внешнему поиску для лабораторных показателей — зеркало
/// <see cref="Enrichment.MedicationSearchCacheService"/> целиком, включая обработку гонки на
/// уникальном индексе (пересборка enrich-пайплайна анализов, закрывает задокументированный ранее
/// пропуск: без этого кэша каждая доработка промпта суммаризатора/схемы полей означала новый
/// платный запрос на каждый показатель заново). Ключ — пара (NormalizedName, SpecimenKbId).
/// </summary>
public class LabAnalyteSearchCacheService(
    AppDbContext db, IOptions<EnrichmentOptions> options, ILogger<LabAnalyteSearchCacheService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Null — по этой паре (название, источник) ещё ни разу не искали платно.</summary>
    public async Task<CachedAnalyteSearch?> GetCachedAsync(
        string normalizedName, Guid specimenKbId, CancellationToken ct = default)
    {
        var cache = await db.LabAnalyteSearchCaches.AsNoTracking()
            .FirstOrDefaultAsync(c => c.NormalizedName == normalizedName && c.SpecimenKbId == specimenKbId, ct);
        if (cache?.SnippetsJson is null) return null;

        var snippets = JsonSerializer.Deserialize<List<WebSnippet>>(cache.SnippetsJson, JsonOptions) ?? [];
        var overrides = ParseOverrides(cache.OverridesJson);
        return new CachedAnalyteSearch(snippets, cache.Provider, cache.LastUpdatedAt, cache.CanBeUpdatedAfter, overrides);
    }

    /// <summary>Точечное включение/выключение конкретного URL в уже закэшированной выдаче (админка) —
    /// null снимает override, дальше решает только членство домена в EnrichmentTrustedDomain.</summary>
    public async Task<bool> SetSnippetOverrideAsync(Guid id, string url, bool? enabled, CancellationToken ct = default)
    {
        var cache = await db.LabAnalyteSearchCaches.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cache is null) return false;

        var overrides = ParseOverrides(cache.OverridesJson)?.ToDictionary(kv => kv.Key, kv => kv.Value)
            ?? new Dictionary<string, bool>();
        if (enabled is null) overrides.Remove(url);
        else overrides[url] = enabled.Value;

        cache.OverridesJson = overrides.Count == 0 ? null : JsonSerializer.Serialize(overrides, JsonOptions);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Постранично, с поиском по подстроке названия — для админки (EnrichmentAdminEndpoints).</summary>
    public async Task<(List<LabAnalyteSearchCache> Rows, int Total)> ListAsync(
        string? query, int skip, int take, CancellationToken ct = default)
    {
        var filtered = db.LabAnalyteSearchCaches.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query))
            filtered = filtered.Where(c => c.NormalizedName.Contains(query));

        var total = await filtered.CountAsync(ct);
        var rows = await filtered.OrderByDescending(c => c.LastUpdatedAt).Skip(skip).Take(take).ToListAsync(ct);
        return (rows, total);
    }

    public async Task<LabAnalyteSearchCache?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await db.LabAnalyteSearchCaches.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

    /// <summary>Массовая очистка кэша от строк с нерезолвленным источником — жёсткий гейт
    /// (LabAnalyteEnrichmentRequestService) не даёт новым задачам с SpecimenKbId=Unresolved
    /// ставиться в очередь, поэтому такие строки кэша (наследие до пересборки enrich-пайплайна,
    /// когда источник был enum SpecimenType.Unknown, см. миграцию ReworkSpecimenAsData) никогда
    /// больше не будут прочитаны ни одной задачей — чистый мусор. Возвращает число удалённых строк.</summary>
    public async Task<int> PurgeUnresolvedSpecimenAsync(CancellationToken ct = default) =>
        await db.LabAnalyteSearchCaches.Where(c => c.SpecimenKbId == SpecimenContextIds.Unresolved).ExecuteDeleteAsync(ct);

    private static Dictionary<string, bool>? ParseOverrides(string? overridesJson) =>
        overridesJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, bool>>(overridesJson, JsonOptions);

    /// <summary>Фиксирует факт обращения к платному API вместе с самими результатами — вызывать
    /// сразу после реального запроса (успешного или нет), безусловно, тем же принципом, что
    /// MedicationSearchCacheService.RecordSearchAsync (см. её doc-комментарий).</summary>
    public async Task RecordSearchAsync(
        string normalizedName, Guid specimenKbId, string provider, IReadOnlyList<WebSnippet> snippets,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var canBeUpdatedAfter = now.AddMonths(options.Value.MinRefreshIntervalMonths);
        var snippetsJson = JsonSerializer.Serialize(snippets, JsonOptions);

        var existing = await db.LabAnalyteSearchCaches
            .FirstOrDefaultAsync(c => c.NormalizedName == normalizedName && c.SpecimenKbId == specimenKbId, ct);
        if (existing is not null)
        {
            ApplyRecord(existing, provider, now, canBeUpdatedAfter, snippetsJson);
            await db.SaveChangesAsync(ct);
            return;
        }

        var cache = new LabAnalyteSearchCache
        {
            Id = Guid.NewGuid(),
            NormalizedName = normalizedName,
            SpecimenKbId = specimenKbId,
            Provider = provider,
            LastUpdatedAt = now,
            CanBeUpdatedAfter = canBeUpdatedAfter,
            SnippetsJson = snippetsJson,
        };
        db.LabAnalyteSearchCaches.Add(cache);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Гонка на уникальном индексе (NormalizedName, SpecimenKbId) — тот же показатель мог
            // обогащаться параллельно из двух разных бланков (см. MedicationSearchCacheService —
            // тот же приём на другой таблице).
            logger.LogDebug(ex, "Кэш поиска показателя «{Name}» ({SpecimenKbId}): гонка на ключе, переигрываем как обновление",
                normalizedName, specimenKbId);
            db.Entry(cache).State = EntityState.Detached;

            var existingAfterRace = await db.LabAnalyteSearchCaches
                .SingleAsync(c => c.NormalizedName == normalizedName && c.SpecimenKbId == specimenKbId, ct);
            ApplyRecord(existingAfterRace, provider, now, canBeUpdatedAfter, snippetsJson);
            await db.SaveChangesAsync(ct);
        }
    }

    private static void ApplyRecord(
        LabAnalyteSearchCache cache, string provider, DateTime now, DateTime canBeUpdatedAfter, string snippetsJson)
    {
        cache.Provider = provider;
        cache.LastUpdatedAt = now;
        cache.CanBeUpdatedAfter = canBeUpdatedAfter;
        cache.SnippetsJson = snippetsJson;
    }
}
