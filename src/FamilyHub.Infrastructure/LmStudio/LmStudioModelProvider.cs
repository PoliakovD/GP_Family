using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FamilyHub.Infrastructure.LmStudio;

/// <summary>
/// Резолвит модель/уровень размышлений LM Studio, которые реально шлёт LmStudioJsonClient —
/// активное значение из БД (LmStudioModelConfig/LmStudioReasoningConfig), фолбэк на захардкоженные
/// LmStudioOptions.Model/Reasoning, если админ ничего не выбирал. Кэш в IMemoryCache — тот же приём
/// и тот же TTL, что PromptProvider/PipelineConfigService: смена случается на порядки реже, чем сам
/// клиент спрашивает, что слать.
/// </summary>
public class LmStudioModelProvider(AppDbContext db, IMemoryCache cache) : ILmStudioModelProvider
{
    private const string CacheKey = "lmstudio:active-model";
    private const string ReasoningCacheKey = "lmstudio:active-reasoning";
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

    public async Task<LmStudioReasoning> GetActiveReasoningAsync(LmStudioReasoning fallback, CancellationToken ct = default)
    {
        // bool, не LmStudioReasoning? — TryGetValue с enum-типом даёт default(LmStudioReasoning)
        // (=None) и true=false и на "нет записи", и на "закэшировано None" одинаково, поэтому кэш
        // отдельным bool-флагом "было ли значение реально сохранено" надёжнее, чем полагаться на
        // сам факт наличия ключа с enum-значением.
        if (cache.TryGetValue<LmStudioReasoning>(ReasoningCacheKey, out var cached)) return cached;

        var row = await db.LmStudioReasoningConfigs.AsNoTracking().FirstOrDefaultAsync(ct);
        var resolved = row?.Reasoning ?? fallback;
        cache.Set(ReasoningCacheKey, resolved, CacheTtl);
        return resolved;
    }

    public void InvalidateReasoning() => cache.Remove(ReasoningCacheKey);
}
