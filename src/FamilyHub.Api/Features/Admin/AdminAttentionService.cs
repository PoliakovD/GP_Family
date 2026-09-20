using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Enrichment;
using FamilyHub.Modules.Medical.Extraction;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Api.Features.Admin;

/// <summary>
/// Инбокс «Требует внимания» (§3 плана) — агрегирует падения всех четырёх конвейеров по
/// FailureReason и вычисляет, какие домены чаще всего отбрасываются фильтром доверия (то, ради
/// чего инбокс и нужен: «эти N доменов разблокируют M задач» вместо ручного разбора каждой задачи
/// по отдельности). Только чтение — вызывающая сторона (AdminPipelineEndpoints) кэширует результат
/// на короткий TTL, тот же приём, что AdminStatsService.GetStorageStatsAsync.
/// </summary>
public class AdminAttentionService(
    AppDbContext db,
    LabAnalyteSearchCacheService labCache,
    MedicationSearchCacheService medCache,
    EnrichmentTrustedDomainService trustedDomains)
{
    /// <summary>Сколько последних Failed/NoTrustedSnippets задач разбирать на домены — не весь
    /// исторический массив: свежие падения важнее, а кэш поиска на них уже наверняка загружен в
    /// память процессом (частые повторные поиски по одному имени редки, см. MinRefreshIntervalMonths).</summary>
    private const int DroppedDomainsSampleSize = 200;

    private const int MaxDroppedDomains = 20;

    public async Task<AdminAttentionDto> GetAttentionAsync(CancellationToken ct = default)
    {
        var reasons = await BuildReasonsAsync(ct);
        var dropped = await BuildDroppedDomainsAsync(ct);
        var webSearchPaused = await BuildWebSearchPausedAsync(ct);
        return new AdminAttentionDto(reasons, dropped, webSearchPaused);
    }

    /// <summary>Deferred-задачи (вентиль закрыт, ADR-0005 §9) — та же природа, что AttentionReasonDto,
    /// но не отказ, поэтому отдельный блок, не строка среди причин падения.</summary>
    private async Task<WebSearchPausedDto> BuildWebSearchPausedAsync(CancellationToken ct)
    {
        var valveRow = await db.WebSearchConfigs.AsNoTracking().FirstOrDefaultAsync(ct);

        var byType = new Dictionary<string, int>
        {
            ["lab-analyte"] = await db.LabAnalyteEnrichmentJobs.CountAsync(j => j.Status == EnrichmentJobStatus.Deferred, ct),
            ["medication"] = await db.MedicationEnrichmentJobs.CountAsync(j => j.Status == EnrichmentJobStatus.Deferred, ct),
            ["visit-medication"] = await db.VisitMedicationEnrichmentJobs.CountAsync(j => j.Status == EnrichmentJobStatus.Deferred, ct),
        };

        return new WebSearchPausedDto(
            valveRow?.IsPaused ?? false, valveRow?.PausedAt, valveRow?.Note, byType.Values.Sum(), byType);
    }

    private async Task<List<AttentionReasonDto>> BuildReasonsAsync(CancellationToken ct)
    {
        var byReason = new Dictionary<string, Dictionary<string, int>>();

        async Task AddAsync(string type, IQueryable<EnrichmentFailureReason?> failedReasons)
        {
            var groups = await failedReasons
                .GroupBy(r => r)
                .Select(g => new { Reason = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            foreach (var g in groups)
            {
                // Задачи, упавшие до этой правки, никогда не получат FailureReason — не теряем их
                // молча, показываем отдельной строкой с понятной подписью (см. LabelFor).
                var key = g.Reason?.ToString() ?? "Unclassified";
                if (!byReason.TryGetValue(key, out var byType))
                {
                    byType = [];
                    byReason[key] = byType;
                }
                byType[type] = byType.GetValueOrDefault(type) + g.Count;
            }
        }

        await AddAsync("lab-analyte", db.LabAnalyteEnrichmentJobs
            .Where(j => j.Status == EnrichmentJobStatus.Failed).Select(j => j.FailureReason));
        await AddAsync("medication", db.MedicationEnrichmentJobs
            .Where(j => j.Status == EnrichmentJobStatus.Failed).Select(j => j.FailureReason));
        await AddAsync("visit-medication", db.VisitMedicationEnrichmentJobs
            .Where(j => j.Status == EnrichmentJobStatus.Failed).Select(j => j.FailureReason));
        await AddAsync("extraction", db.MedicalDocumentExtractionJobs
            .Where(j => j.Status == EnrichmentJobStatus.Failed).Select(j => j.FailureReason));

        return byReason
            .Select(kv => new AttentionReasonDto(kv.Key, LabelFor(kv.Key), kv.Value.Values.Sum(), kv.Value))
            .OrderByDescending(r => r.Count)
            .ToList();
    }

    private async Task<List<DroppedDomainDto>> BuildDroppedDomainsAsync(CancellationToken ct)
    {
        var labActiveDomains = await trustedDomains.GetActiveDomainsByPriorityAsync(WebSearchTopic.LabAnalyte, ct);
        var medActiveDomains = await trustedDomains.GetActiveDomainsByPriorityAsync(WebSearchTopic.Medication, ct);

        var byDomain = new Dictionary<(string Domain, WebSearchTopic Topic), (HashSet<Guid> JobIds, string SampleUrl)>();

        void Track(string domain, WebSearchTopic topic, Guid jobId, string sampleUrl)
        {
            var key = (domain, topic);
            if (!byDomain.TryGetValue(key, out var entry))
            {
                entry = ([], sampleUrl);
                byDomain[key] = entry;
            }
            entry.JobIds.Add(jobId);
        }

        // Показатели — ключ (NormalizedName, SpecimenKbId), поэтому кэшируем строки поиска по этой
        // паре, чтобы не спрашивать один и тот же (частый) показатель у БД повторно.
        var labJobs = await db.LabAnalyteEnrichmentJobs.AsNoTracking()
            .Where(j => j.Status == EnrichmentJobStatus.Failed && j.FailureReason == EnrichmentFailureReason.NoTrustedSnippets)
            .OrderByDescending(j => j.CreatedAt)
            .Take(DroppedDomainsSampleSize)
            .Select(j => new { j.Id, j.NormalizedName, j.SpecimenKbId })
            .ToListAsync(ct);

        var labCacheByKey = new Dictionary<(string, Guid), CachedAnalyteSearch?>();
        foreach (var job in labJobs)
        {
            var key = (job.NormalizedName, job.SpecimenKbId);
            if (!labCacheByKey.TryGetValue(key, out var cached))
            {
                cached = await labCache.GetCachedAsync(job.NormalizedName, job.SpecimenKbId, ct);
                labCacheByKey[key] = cached;
            }
            if (cached is null) continue;

            foreach (var snippet in cached.Snippets)
            {
                if (EnrichmentSnippetFilter.IsEnabled(snippet.Url, labActiveDomains, cached.Overrides)) continue;
                if (!Uri.TryCreate(snippet.Url, UriKind.Absolute, out var uri)) continue;
                Track(uri.Host, WebSearchTopic.LabAnalyte, job.Id, snippet.Url);
            }
        }

        // Медикаменты + препараты из заключений врача делят один справочник/кэш/список доменов
        // (см. VisitMedicationEnrichmentJob class doc) — разбираем обе очереди задач в один проход.
        var medJobs = await db.MedicationEnrichmentJobs.AsNoTracking()
            .Where(j => j.Status == EnrichmentJobStatus.Failed && j.FailureReason == EnrichmentFailureReason.NoTrustedSnippets)
            .OrderByDescending(j => j.CreatedAt)
            .Take(DroppedDomainsSampleSize)
            .Select(j => new { j.Id, j.NormalizedName })
            .ToListAsync(ct);
        var visitJobs = await db.VisitMedicationEnrichmentJobs.AsNoTracking()
            .Where(j => j.Status == EnrichmentJobStatus.Failed && j.FailureReason == EnrichmentFailureReason.NoTrustedSnippets)
            .OrderByDescending(j => j.CreatedAt)
            .Take(DroppedDomainsSampleSize)
            .Select(j => new { j.Id, j.NormalizedName })
            .ToListAsync(ct);

        var medCacheByName = new Dictionary<string, CachedSearch?>();
        foreach (var job in medJobs.Concat(visitJobs))
        {
            if (!medCacheByName.TryGetValue(job.NormalizedName, out var cached))
            {
                cached = await medCache.GetCachedAsync(job.NormalizedName, ct);
                medCacheByName[job.NormalizedName] = cached;
            }
            if (cached is null) continue;

            foreach (var snippet in cached.Snippets)
            {
                if (EnrichmentSnippetFilter.IsEnabled(snippet.Url, medActiveDomains, cached.Overrides)) continue;
                if (!Uri.TryCreate(snippet.Url, UriKind.Absolute, out var uri)) continue;
                Track(uri.Host, WebSearchTopic.Medication, job.Id, snippet.Url);
            }
        }

        return byDomain
            .Select(kv => new DroppedDomainDto(kv.Key.Domain, kv.Key.Topic.ToString(), kv.Value.JobIds.Count, kv.Value.SampleUrl))
            .OrderByDescending(d => d.JobCount)
            .Take(MaxDroppedDomains)
            .ToList();
    }

    private static string LabelFor(string reason) => reason switch
    {
        nameof(EnrichmentFailureReason.Legitimacy) => "Не прошла проверка легитимности",
        nameof(EnrichmentFailureReason.Plausibility) => "Не прошла проверка правдоподобности",
        nameof(EnrichmentFailureReason.NoTrustedSnippets) => "Нет доверенных сниппетов",
        nameof(EnrichmentFailureReason.NoSourcesCited) => "Модель не сослалась на источник",
        nameof(EnrichmentFailureReason.SummarizerFailed) => "Сбой суммаризации",
        nameof(EnrichmentFailureReason.IsolationViolation) => "Подозрение на персональные данные",
        nameof(EnrichmentFailureReason.LmStudioUnavailable) => "LM Studio недоступна",
        nameof(EnrichmentFailureReason.ProviderFailed) => "Сбой провайдера поиска",
        nameof(EnrichmentFailureReason.Unknown) => "Неизвестная ошибка",
        "Unclassified" => "Причина не определена (задача упала до этого обновления)",
        _ => reason,
    };
}
