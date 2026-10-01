using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Modules.Medical.Enrichment;

/// <summary>Результаты последнего платного поиска по названию + метаданные свежести — то, что
/// действительно хранится (не просто факт обращения, а сами сниппеты, полученные от провайдера).
/// Overrides — точечные ручные включения/выключения конкретных URL из админки, поверх решения по
/// домену (см. EnrichmentSnippetFilter).</summary>
public record CachedSearch(
    IReadOnlyList<WebSnippet> Snippets, string Provider, DateTime LastUpdatedAt, DateTime CanBeUpdatedAfter,
    IReadOnlyDictionary<string, bool>? Overrides = null)
{
    /// <summary>Пока не истёк минимальный интервал обновления — новый платный запрос не нужен,
    /// эти сниппеты можно пересуммаризировать сколько угодно раз бесплатно.</summary>
    public bool IsFresh => CanBeUpdatedAfter > DateTime.UtcNow;
}

/// <summary>
/// Настоящий кэш обращений к платному внешнему поиску (MedicationSearchCache) — хранит сами
/// сниппеты, полученные от провайдера, а не только факт "когда обращались". Это отличает его от
/// KbLookupService: тот проверяет, есть ли уже ЗНАНИЕ о препарате (готовая карточка для
/// пользователя); этот — есть ли уже ОПЛАЧЕННЫЕ сырые результаты поиска, которые можно повторно
/// скормить суммаризатору без нового платного запроса (например, при доработке промпта/схемы
/// полей MedicationSummary в разработке — см. MedicationEnrichmentProcessor).
/// </summary>
public class MedicationSearchCacheService(
    AppDbContext db, IOptions<EnrichmentOptions> options, ILogger<MedicationSearchCacheService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = SearchCacheSnippets.JsonOptions;

    /// <summary>Null — по этому названию ещё ни разу не искали платно.</summary>
    public async Task<CachedSearch?> GetCachedAsync(string normalizedName, CancellationToken ct = default)
    {
        var cache = await db.MedicationSearchCaches.AsNoTracking()
            .FirstOrDefaultAsync(c => c.NormalizedName == normalizedName, ct);
        if (cache?.SnippetsJson is null) return null;

        var snippets = SearchCacheSnippets.Parse(cache.SnippetsJson);
        var overrides = ParseOverrides(cache.OverridesJson);
        return new CachedSearch(snippets, cache.Provider, cache.LastUpdatedAt, cache.CanBeUpdatedAfter, overrides);
    }

    /// <summary>Точечное включение/выключение конкретного URL в уже закэшированной выдаче (админка) —
    /// null снимает override, дальше решает только членство домена в EnrichmentTrustedDomain.</summary>
    public async Task<bool> SetSnippetOverrideAsync(Guid id, string url, bool? enabled, CancellationToken ct = default)
    {
        var cache = await db.MedicationSearchCaches.FirstOrDefaultAsync(c => c.Id == id, ct);
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
    public async Task<(List<MedicationSearchCache> Rows, int Total)> ListAsync(
        string? query, int skip, int take, CancellationToken ct = default)
    {
        var filtered = db.MedicationSearchCaches.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query))
            filtered = filtered.Where(c => c.NormalizedName.Contains(query));

        var total = await filtered.CountAsync(ct);
        var rows = await filtered.OrderByDescending(c => c.LastUpdatedAt).Skip(skip).Take(take).ToListAsync(ct);
        return (rows, total);
    }

    public async Task<MedicationSearchCache?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await db.MedicationSearchCaches.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

    /// <summary>Обратный поиск строки кэша по названию — карточка задачи в админке знает
    /// NormalizedName (из самой задачи), не Id строки кэша. Общий кэш для Medication и
    /// VisitMedication задач (см. VisitMedicationEnrichmentJob class doc).</summary>
    public async Task<MedicationSearchCache?> GetByNameAsync(string normalizedName, CancellationToken ct = default) =>
        await db.MedicationSearchCaches.AsNoTracking().FirstOrDefaultAsync(c => c.NormalizedName == normalizedName, ct);

    /// <summary>Полное редактирование строки кэша из админки — см. LabAnalyteSearchCacheService.UpdateAsync,
    /// тот же приём на другую таблицу.</summary>
    public async Task<bool> UpdateAsync(
        Guid id, string? provider, IReadOnlyList<WebSnippet> snippets, CancellationToken ct = default)
    {
        var cache = await db.MedicationSearchCaches.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cache is null) return false;

        if (provider is not null) cache.Provider = provider;
        // Ручные сниппеты (ADR-0018), которых нет в присланном списке, остаются — старый редактор
        // кэша про них не знает; явно удалить ручной сниппет можно через очередь «Одобрение».
        var merged = SearchCacheSnippets.MergeAfterReplace(SearchCacheSnippets.Parse(cache.SnippetsJson), snippets);
        cache.SnippetsJson = SearchCacheSnippets.Serialize(merged);

        var overrides = ParseOverrides(cache.OverridesJson);
        if (overrides is not null)
        {
            var urls = merged.Select(s => s.Url).ToHashSet();
            var pruned = overrides.Where(kv => urls.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            cache.OverridesJson = pruned.Count == 0 ? null : JsonSerializer.Serialize(pruned, JsonOptions);
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) =>
        await db.MedicationSearchCaches.Where(c => c.Id == id).ExecuteDeleteAsync(ct) > 0;

    /// <summary>Строка кэша по ключу; если её нет — создаёт пустую «устаревшую» (CanBeUpdatedAfter = сейчас): очередь
    /// «Одобрение» (ADR-0018) позволяет админу добавить ручные сниппеты ещё ДО первого платного поиска, а платный
    /// поиск после этого по-прежнему разрешён (строка не свежая) и сохранит ручные сниппеты.</summary>
    public async Task<MedicationSearchCache> GetOrCreateAsync(string normalizedName, CancellationToken ct = default)
    {
        var existing = await db.MedicationSearchCaches.FirstOrDefaultAsync(c => c.NormalizedName == normalizedName, ct);
        if (existing is not null) return existing;

        var now = DateTime.UtcNow;
        var cache = new MedicationSearchCache
        {
            Id = Guid.NewGuid(), NormalizedName = normalizedName, Provider = "manual",
            LastUpdatedAt = now, CanBeUpdatedAfter = now, SnippetsJson = "[]",
        };
        db.MedicationSearchCaches.Add(cache);
        try
        {
            await db.SaveChangesAsync(ct);
            return cache;
        }
        catch (DbUpdateException)
        {
            db.Entry(cache).State = EntityState.Detached;
            return await db.MedicationSearchCaches.SingleAsync(c => c.NormalizedName == normalizedName, ct);
        }
    }

    private static Dictionary<string, bool>? ParseOverrides(string? overridesJson) =>
        overridesJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, bool>>(overridesJson, JsonOptions);

    /// <summary>Фиксирует факт обращения к платному API вместе с самими результатами — вызывать
    /// сразу после реального запроса (успешного или нет: деньги уже потрачены в любом случае,
    /// а пустой список тоже стоит кэшировать — не имеет смысла платно спрашивать снова
    /// раньше срока то же название, если в прошлый раз ничего не нашлось).</summary>
    public async Task RecordSearchAsync(
        string normalizedName, string provider, IReadOnlyList<WebSnippet> snippets, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var canBeUpdatedAfter = now.AddMonths(options.Value.MinRefreshIntervalMonths);
        var snippetsJson = JsonSerializer.Serialize(snippets, JsonOptions);

        var existing = await db.MedicationSearchCaches.FirstOrDefaultAsync(c => c.NormalizedName == normalizedName, ct);
        if (existing is not null)
        {
            ApplyRecord(existing, provider, now, canBeUpdatedAfter, MergeKeepingManual(existing.SnippetsJson, snippets));
            await db.SaveChangesAsync(ct);
            return;
        }

        var cache = new MedicationSearchCache
        {
            Id = Guid.NewGuid(),
            NormalizedName = normalizedName,
            Provider = provider,
            LastUpdatedAt = now,
            CanBeUpdatedAfter = canBeUpdatedAfter,
            SnippetsJson = snippetsJson,
        };
        db.MedicationSearchCaches.Add(cache);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Гонка на уникальном индексе NormalizedName (аудит, находка High #3) — тот же
            // препарат мог обогащаться параллельно через конвейер аптечки и конвейер заключений
            // врача (см. VisitMedicationEnrichmentRequestService, где эта гонка уже описана как
            // ожидаемая, но раньше некому было её здесь поймать — падало необработанным 500).
            logger.LogDebug(ex, "Кэш поиска «{Name}»: гонка на NormalizedName, переигрываем как обновление", normalizedName);
            db.Entry(cache).State = EntityState.Detached;

            var existingAfterRace = await db.MedicationSearchCaches.SingleAsync(c => c.NormalizedName == normalizedName, ct);
            ApplyRecord(existingAfterRace, provider, now, canBeUpdatedAfter, MergeKeepingManual(existingAfterRace.SnippetsJson, snippets));
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Свежая выдача заменяет авто-сниппеты, ручные и закреплённые остаются (ADR-0018).</summary>
    private static string MergeKeepingManual(string? existingSnippetsJson, IReadOnlyList<WebSnippet> fresh) =>
        SearchCacheSnippets.Serialize(SearchCacheSnippets.MergeAfterSearch(SearchCacheSnippets.Parse(existingSnippetsJson), fresh));

    private static void ApplyRecord(
        MedicationSearchCache cache, string provider, DateTime now, DateTime canBeUpdatedAfter, string snippetsJson)
    {
        cache.Provider = provider;
        cache.LastUpdatedAt = now;
        cache.CanBeUpdatedAfter = canBeUpdatedAfter;
        cache.SnippetsJson = snippetsJson;
    }
}
