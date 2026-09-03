using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FamilyHub.Modules.Medical.Pipeline;

/// <summary>
/// Отвечает "включён ли этот шаг" (управление enrich-пайплайном из админки, §2 плана) —
/// обязательные шаги (PipelineCatalog, IsMandatory=true) БД не спрашивает вовсе, их нельзя
/// выключить ни при каких обстоятельствах. Отсутствие строки PipelineStepConfig для
/// необязательного шага означает "включён" — заводить записи заранее не нужно, только когда
/// админ реально что-то выключает. Кэш — тот же приём и тот же TTL, что PromptProvider.
/// </summary>
public class PipelineConfigService(AppDbContext db, IMemoryCache cache) : IPipelineConfigService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public async Task<bool> IsEnabledAsync(string pipelineKey, string stepKey, CancellationToken ct = default)
    {
        var declaration = PipelineCatalog.Find(pipelineKey, stepKey);
        if (declaration is null || declaration.IsMandatory) return true;

        var cacheKey = CacheKey(pipelineKey, stepKey);
        if (cache.TryGetValue(cacheKey, out bool cached)) return cached;

        var isEnabled = await db.PipelineStepConfigs.AsNoTracking()
            .Where(s => s.PipelineKey == pipelineKey && s.StepKey == stepKey)
            .Select(s => (bool?)s.IsEnabled)
            .FirstOrDefaultAsync(ct) ?? true;

        cache.Set(cacheKey, isEnabled, CacheTtl);
        return isEnabled;
    }

    public void Invalidate(string pipelineKey, string stepKey) => cache.Remove(CacheKey(pipelineKey, stepKey));

    private static string CacheKey(string pipelineKey, string stepKey) => $"pipeline:step:{pipelineKey}:{stepKey}";
}
