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

/// <summary>Группа поиска биоматериала (ADR-0018): EffectiveKey — ключ строки кэша; QueryLabel — слово, которое
/// подставляется в поисковый запрос вместо названия биоматериала (текст группы либо название самого биоматериала).</summary>
public record SpecimenSearchGroup(string EffectiveKey, string? GroupKey, string QueryLabel);

/// <summary>«Двойник» (ADR-0018): свежий кэш того же названия показателя у ДРУГОЙ группы поиска — админ может взять его
/// вместо платного поиска. Автоматически схлопываются только биоматериалы одной группы; между группами — лишь подсказка.</summary>
public record CacheTwin(
    Guid CacheId, Guid SpecimenKbId, string SpecimenDisplayName, string? SearchGroupKey, string Provider,
    DateTime LastUpdatedAt, DateTime CanBeUpdatedAfter, int SnippetCount);

/// <summary>
/// Настоящий кэш обращений к платному внешнему поиску для лабораторных показателей — зеркало
/// <see cref="Enrichment.MedicationSearchCacheService"/> целиком, включая обработку гонки на
/// уникальном индексе (пересборка enrich-пайплайна анализов, закрывает задокументированный ранее
/// пропуск: без этого кэша каждая доработка промпта суммаризатора/схемы полей означала новый
/// платный запрос на каждый показатель заново). Ключ — пара (NormalizedName, группа поиска биоматериала): биоматериалы одной
/// группы (GlobalSpecimenKb.SearchGroupKey) делят строку и платный запрос (ADR-0018).
/// </summary>
public class LabAnalyteSearchCacheService(
    AppDbContext db, IOptions<EnrichmentOptions> options, ILogger<LabAnalyteSearchCacheService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = SearchCacheSnippets.JsonOptions;

    /// <summary>Группа поиска биоматериала: эффективный ключ строки кэша и слово для поискового запроса.</summary>
    public async Task<SpecimenSearchGroup> GetSearchGroupAsync(Guid specimenKbId, CancellationToken ct = default)
    {
        var specimen = await db.GlobalSpecimensKb.AsNoTracking()
            .Where(s => s.Id == specimenKbId)
            .Select(s => new { s.DisplayName, s.SearchGroupKey })
            .FirstOrDefaultAsync(ct);
        var groupKey = SearchGroupKeys.Normalize(specimen?.SearchGroupKey);
        return new SpecimenSearchGroup(
            SearchGroupKeys.Effective(specimenKbId, groupKey), groupKey, groupKey ?? specimen?.DisplayName ?? string.Empty);
    }

    /// <summary>Null — по этой паре (название, группа поиска) ещё ни разу не искали платно.</summary>
    public async Task<CachedAnalyteSearch?> GetCachedAsync(
        string normalizedName, Guid specimenKbId, CancellationToken ct = default)
    {
        var group = await GetSearchGroupAsync(specimenKbId, ct);
        var cache = await db.LabAnalyteSearchCaches.AsNoTracking()
            .FirstOrDefaultAsync(c => c.NormalizedName == normalizedName && c.SearchGroupKey == group.EffectiveKey, ct);
        if (cache?.SnippetsJson is null) return null;

        var snippets = SearchCacheSnippets.Parse(cache.SnippetsJson);
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

    /// <summary>Обратный поиск строки кэша по ключу (название, источник) — карточка задачи в
    /// админке знает NormalizedName+SpecimenKbId (из самой задачи), не Id строки кэша.</summary>
    public async Task<LabAnalyteSearchCache?> GetByNameAsync(
        string normalizedName, Guid specimenKbId, CancellationToken ct = default)
    {
        var group = await GetSearchGroupAsync(specimenKbId, ct);
        return await db.LabAnalyteSearchCaches.AsNoTracking()
            .FirstOrDefaultAsync(c => c.NormalizedName == normalizedName && c.SearchGroupKey == group.EffectiveKey, ct);
    }

    /// <summary>Свежие строки кэша того же названия у ДРУГИХ групп поиска — «двойники» для очереди «Одобрение»
    /// (ADR-0018): вместо нового платного поиска админ может взять кэш двойника.</summary>
    public async Task<List<CacheTwin>> FindTwinsAsync(string normalizedName, Guid specimenKbId, CancellationToken ct = default)
    {
        var group = await GetSearchGroupAsync(specimenKbId, ct);
        var now = DateTime.UtcNow;
        var rows = await db.LabAnalyteSearchCaches.AsNoTracking()
            .Where(c => c.NormalizedName == normalizedName && c.SearchGroupKey != group.EffectiveKey
                && c.CanBeUpdatedAfter > now && c.SnippetsJson != null && c.SnippetsJson != "[]")
            .OrderByDescending(c => c.LastUpdatedAt)
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        var specimenIds = rows.Select(r => r.SpecimenKbId).Distinct().ToList();
        var specimens = await db.GlobalSpecimensKb.AsNoTracking()
            .Where(s => specimenIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => new { s.DisplayName, s.SearchGroupKey }, ct);

        return rows.Select(r =>
        {
            var spec = specimens.GetValueOrDefault(r.SpecimenKbId);
            return new CacheTwin(
                r.Id, r.SpecimenKbId, spec?.DisplayName ?? r.SpecimenKbId.ToString(), SearchGroupKeys.Normalize(spec?.SearchGroupKey),
                r.Provider, r.LastUpdatedAt, r.CanBeUpdatedAfter, SearchCacheSnippets.Parse(r.SnippetsJson).Count);
        }).ToList();
    }

    /// <summary>Полное редактирование строки кэша из админки — снипеты (заголовок/ссылка/текст)
    /// заменяются целиком присланным списком (тот же приём, что у payload-редактора справочника:
    /// админ видит и правит весь список сразу, не позиционными add/edit/remove запросами).
    /// Provider не трогается, если не передан (null — оставить как есть). Overrides,
    /// ссылающиеся на URL, которых больше нет в новом списке, вычищаются — иначе они бы
    /// молча висели в БД, ни на что не влияя (EnrichmentSnippetFilter сверяет override только
    /// с URL реально пришедших сниппетов).</summary>
    public async Task<bool> UpdateAsync(
        Guid id, string? provider, IReadOnlyList<WebSnippet> snippets, CancellationToken ct = default)
    {
        var cache = await db.LabAnalyteSearchCaches.FirstOrDefaultAsync(c => c.Id == id, ct);
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

    /// <summary>Удаление строки целиком — следующая задача обогащения по этому (названию,
    /// источнику) увидит "не кэшировано" и оплатит новый поиск, как если бы кэша никогда не
    /// было (см. AdminAttentionService/AdminPipelineEndpoints — по Id их ничего не держит,
    /// см. class doc сервиса).</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) =>
        await db.LabAnalyteSearchCaches.Where(c => c.Id == id).ExecuteDeleteAsync(ct) > 0;

    /// <summary>Массовая очистка кэша от строк с нерезолвленным источником — жёсткий гейт
    /// (LabAnalyteEnrichmentRequestService) не даёт новым задачам с SpecimenKbId=Unresolved
    /// ставиться в очередь, поэтому такие строки кэша (наследие до пересборки enrich-пайплайна,
    /// когда источник был enum SpecimenType.Unknown, см. миграцию ReworkSpecimenAsData) никогда
    /// больше не будут прочитаны ни одной задачей — чистый мусор. Возвращает число удалённых строк.</summary>
    public async Task<int> PurgeUnresolvedSpecimenAsync(CancellationToken ct = default) =>
        // Строки с ручными сниппетами (ADR-0018) не чистим — это работа админа, а не мусор автопоиска.
        await db.LabAnalyteSearchCaches
            .Where(c => c.SpecimenKbId == SpecimenContextIds.Unresolved
                && (c.SnippetsJson == null || !c.SnippetsJson.Contains("\"Manual\"")))
            .ExecuteDeleteAsync(ct);

    /// <summary>Строка кэша по ключу; если её нет — создаёт пустую «устаревшую» (см. MedicationSearchCacheService.GetOrCreateAsync).</summary>
    public async Task<LabAnalyteSearchCache> GetOrCreateAsync(string normalizedName, Guid specimenKbId, CancellationToken ct = default)
    {
        var group = await GetSearchGroupAsync(specimenKbId, ct);
        var existing = await db.LabAnalyteSearchCaches
            .FirstOrDefaultAsync(c => c.NormalizedName == normalizedName && c.SearchGroupKey == group.EffectiveKey, ct);
        if (existing is not null) return existing;

        var now = DateTime.UtcNow;
        var cache = new LabAnalyteSearchCache
        {
            Id = Guid.NewGuid(), NormalizedName = normalizedName, SpecimenKbId = specimenKbId,
            SearchGroupKey = group.EffectiveKey, Provider = "manual",
            LastUpdatedAt = now, CanBeUpdatedAfter = now, SnippetsJson = "[]",
        };
        db.LabAnalyteSearchCaches.Add(cache);
        try
        {
            await db.SaveChangesAsync(ct);
            return cache;
        }
        catch (DbUpdateException)
        {
            db.Entry(cache).State = EntityState.Detached;
            return await db.LabAnalyteSearchCaches
                .SingleAsync(c => c.NormalizedName == normalizedName && c.SearchGroupKey == group.EffectiveKey, ct);
        }
    }

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

        var group = await GetSearchGroupAsync(specimenKbId, ct);
        var existing = await db.LabAnalyteSearchCaches
            .FirstOrDefaultAsync(c => c.NormalizedName == normalizedName && c.SearchGroupKey == group.EffectiveKey, ct);
        if (existing is not null)
        {
            ApplyRecord(existing, provider, now, canBeUpdatedAfter, MergeKeepingManual(existing.SnippetsJson, snippets));
            await db.SaveChangesAsync(ct);
            return;
        }

        var cache = new LabAnalyteSearchCache
        {
            Id = Guid.NewGuid(),
            NormalizedName = normalizedName,
            SpecimenKbId = specimenKbId,
            SearchGroupKey = group.EffectiveKey,
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
                .SingleAsync(c => c.NormalizedName == normalizedName && c.SearchGroupKey == group.EffectiveKey, ct);
            ApplyRecord(existingAfterRace, provider, now, canBeUpdatedAfter, MergeKeepingManual(existingAfterRace.SnippetsJson, snippets));
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Свежая выдача заменяет авто-сниппеты, ручные и закреплённые остаются (ADR-0018).</summary>
    private static string MergeKeepingManual(string? existingSnippetsJson, IReadOnlyList<WebSnippet> fresh) =>
        SearchCacheSnippets.Serialize(SearchCacheSnippets.MergeAfterSearch(SearchCacheSnippets.Parse(existingSnippetsJson), fresh));

    private static void ApplyRecord(
        LabAnalyteSearchCache cache, string provider, DateTime now, DateTime canBeUpdatedAfter, string snippetsJson)
    {
        cache.Provider = provider;
        cache.LastUpdatedAt = now;
        cache.CanBeUpdatedAfter = canBeUpdatedAfter;
        cache.SnippetsJson = snippetsJson;
    }
}
