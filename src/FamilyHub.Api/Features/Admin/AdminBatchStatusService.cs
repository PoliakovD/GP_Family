using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Extraction;
using Hangfire;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Api.Features.Admin;

/// <summary>Состояние одной ручной пакетной операции «Пересборок». Remaining — сколько ещё не обработано
/// (по данным, а не по счётчику прогона: операции батчевые и переставляют себя сами, отдельной таблицы
/// прогонов у них нет). Active — задача этого типа сейчас в очереди Hangfire, выполняется или ждёт повтора.</summary>
public record BatchJobStatusDto(string Key, int Remaining, int Total, bool Active);

public record BatchJobsStatusResponse(List<BatchJobStatusDto> Jobs, DateTime CheckedAt);

/// <summary>
/// Прогресс ручных пакетных операций из «Пересборок» — раньше после «поставлено в очередь» админ не видел,
/// идёт ли работа и сколько осталось.
/// </summary>
public class AdminBatchStatusService(AppDbContext db, ILogger<AdminBatchStatusService> logger)
{
    public const string CacheUnits = "cache-units";
    public const string IndicatorFlags = "indicator-flags";
    public const string Reenrich = "reenrich";

    /// <summary>Сколько задач каждой очереди Hangfire просматривать — пакетные операции ставят себя по одной.</summary>
    private const int MaxJobsToScan = 500;

    public async Task<BatchJobsStatusResponse> GetAsync(CancellationToken ct = default)
    {
        var active = ActiveJobTypes();

        var cacheTotal = await db.LabAnalyteSearchCaches.CountAsync(c => c.SnippetsJson != null && c.SnippetsJson != "[]", ct);
        var cacheRemaining = await db.LabAnalyteSearchCaches
            .CountAsync(c => c.Units == null && c.SnippetsJson != null && c.SnippetsJson != "[]", ct);

        var indicatorsTotal = await db.LabIndicators.CountAsync(ct);
        var indicatorsUnknown = await db.LabIndicators.CountAsync(i => i.Flag == IndicatorFlag.Unknown, ct);

        var kbTotal = await db.GlobalLabAnalytesKb.CountAsync(ct);
        var kbStale = await db.GlobalLabAnalytesKb.CountAsync(k => k.PayloadVersion < LabAnalyteSummarySchema.CurrentVersion, ct);
        // Переобогащение идёт обычными задачами конвейера — активно, пока такие задачи живы.
        var maintenanceJobsAlive = await db.LabAnalyteEnrichmentJobs.AnyAsync(j =>
            j.Origin == EnrichmentRequestOrigin.SystemMaintenance
            && (j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running), ct);

        return new BatchJobsStatusResponse(
        [
            new(CacheUnits, cacheRemaining, cacheTotal, active.Contains(nameof(LabAnalyteCacheUnitsBackfillJob))),
            new(IndicatorFlags, indicatorsUnknown, indicatorsTotal, active.Contains(nameof(RecomputeIndicatorFlagsBackfillJob))),
            new(Reenrich, kbStale, kbTotal, active.Contains(nameof(LabAnalyteKbReenrichJob)) || maintenanceJobsAlive),
        ], DateTime.UtcNow);
    }

    /// <summary>Имена типов задач, которые сейчас стоят в очередях, выполняются или ждут повтора. Сбой
    /// хранилища Hangfire не роняет страницу — просто «неизвестно, идёт ли» (Active = false).</summary>
    private HashSet<string> ActiveJobTypes()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var monitoring = JobStorage.Current.GetMonitoringApi();
            foreach (var queue in monitoring.Queues())
                foreach (var job in monitoring.EnqueuedJobs(queue.Name, 0, MaxJobsToScan))
                    Add(job.Value?.Job);
            foreach (var job in monitoring.ProcessingJobs(0, MaxJobsToScan)) Add(job.Value?.Job);
            foreach (var job in monitoring.ScheduledJobs(0, MaxJobsToScan)) Add(job.Value?.Job);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось прочитать очереди Hangfire для статуса пакетных операций");
        }
        return result;

        void Add(Hangfire.Common.Job? job)
        {
            if (job is not null) result.Add(job.Type.Name);
        }
    }
}
