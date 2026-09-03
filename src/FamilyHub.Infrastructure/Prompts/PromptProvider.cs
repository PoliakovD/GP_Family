using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FamilyHub.Infrastructure.Prompts;

/// <summary>
/// Резолвит текст промпта/шаблона по ключу (управление enrich-пайплайном из админки, §2 плана) —
/// активная версия из БД (PipelinePromptVersion), фолбэк на константу в коде, если активной
/// версии нет (пустая БД, только что задеплоенный слот, ещё не отредактированный из админки).
/// Кэш в IMemoryCache (тот же приём, что 15-минутный кэш AdminStatsService) — правка промпта
/// случается на порядки реже, чем сам конвейер спрашивает его текст. См. class doc IPromptProvider
/// про то, почему этот класс в Infrastructure, а не в Modules.Medical.
/// </summary>
public class PromptProvider(AppDbContext db, IMemoryCache cache) : IPromptProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public async Task<string> GetAsync(string key, string fallback, CancellationToken ct = default)
    {
        var cacheKey = CacheKey(key);
        if (cache.TryGetValue(cacheKey, out string? cached) && cached is not null) return cached;

        var activeBody = await db.PipelinePromptVersions.AsNoTracking()
            .Where(v => v.IsActive && v.Prompt.Key == key)
            .Select(v => v.Body)
            .FirstOrDefaultAsync(ct);

        var resolved = string.IsNullOrWhiteSpace(activeBody) ? fallback : activeBody;
        cache.Set(cacheKey, resolved, CacheTtl);
        return resolved;
    }

    /// <summary>Вызывать сразу после создания/активации новой версии — иначе следующий прогон
    /// конвейера мог бы до 5 минут использовать уже неактуальный закэшированный текст.</summary>
    public void Invalidate(string key) => cache.Remove(CacheKey(key));

    private static string CacheKey(string key) => $"pipeline:prompt:{key}";
}
