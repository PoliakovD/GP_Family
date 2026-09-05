using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FamilyHub.Infrastructure.LmStudio;

/// <summary>
/// Резолвит модель LM Studio, которую реально шлёт LmStudioJsonClient в поле "model" запроса
/// chat/completions — активное значение из БД (LmStudioModelConfig), фолбэк на захардкоженный
/// LmStudioOptions.Model, если админ ничего не выбирал. Кэш в IMemoryCache — тот же приём и тот же
/// TTL, что PromptProvider/PipelineConfigService: смена модели случается на порядки реже, чем сам
/// клиент спрашивает, какую слать.
/// </summary>
public class LmStudioModelProvider(AppDbContext db, IMemoryCache cache) : ILmStudioModelProvider
{
    private const string CacheKey = "lmstudio:active-model";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public async Task<string> GetActiveModelAsync(string fallback, CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out string? cached) && cached is not null) return cached;

        var configured = await db.LmStudioModelConfigs.AsNoTracking()
            .Select(c => c.ModelId)
            .FirstOrDefaultAsync(ct);

        var resolved = string.IsNullOrWhiteSpace(configured) ? fallback : configured;
        cache.Set(CacheKey, resolved, CacheTtl);
        return resolved;
    }

    public void Invalidate() => cache.Remove(CacheKey);
}
