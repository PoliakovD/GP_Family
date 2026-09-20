using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Extraction;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Enrichment;

/// <summary>
/// Открытие вентиля платного поиска (ADR-0005 §9) ставит эту джобу — она находит все задачи трёх
/// enrich-конвейеров в статусе <see cref="EnrichmentJobStatus.Deferred"/> (плюс приостановленный
/// прогон прогрева, см. <see cref="SearchWarmupRun"/>) и возобновляет их. Батч + самопродолжение
/// (тот же приём, что <c>LabAnalyteKbReenrichJob</c>) — не монополизирует единственный воркер
/// очереди "enrichment" при большом накопленном хвосте отложенных задач.
///
/// Каждую строку захватывает ИДЕМПОТЕНТНО — <c>ExecuteUpdateAsync</c> с условием на ТЕКУЩИЙ
/// статус, энкью только если реально обновилась ровно одна строка. Это не формальность: обычный
/// <c>RunAsync</c> процессоров не проверяет статус на входе, поэтому повторный вызов джобы
/// (например, админ дважды щёлкнул тумблер, или сработали одновременно ручной запуск и
/// повторный тик) без этой защиты перезапустил бы уже подхваченную соседом задачу — а для
/// <c>LabAnalyteEnrichmentJob.Force = true</c> это означало бы заплатить за поиск дважды.
/// </summary>
[Queue("enrichment")]
public class DeferredEnrichmentReleaseJob(
    AppDbContext db, IBackgroundJobClient backgroundJobs, ILogger<DeferredEnrichmentReleaseJob> logger)
{
    public const int BatchSize = 50;

    public async Task RunAsync(CancellationToken ct = default)
    {
        var medicationIds = await db.MedicationEnrichmentJobs.AsNoTracking()
            .Where(j => j.Status == EnrichmentJobStatus.Deferred)
            .OrderBy(j => j.CreatedAt).Take(BatchSize).Select(j => j.Id).ToListAsync(ct);
        foreach (var id in medicationIds)
        {
            var claimed = await db.MedicationEnrichmentJobs
                .Where(j => j.Id == id && j.Status == EnrichmentJobStatus.Deferred)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, EnrichmentJobStatus.Pending)
                    .SetProperty(j => j.Error, (string?)null), ct);
            if (claimed == 1)
                backgroundJobs.Enqueue<MedicationEnrichmentProcessor>(p => p.RunAsync(id, CancellationToken.None));
        }

        var analyteIds = await db.LabAnalyteEnrichmentJobs.AsNoTracking()
            .Where(j => j.Status == EnrichmentJobStatus.Deferred)
            .OrderBy(j => j.CreatedAt).Take(BatchSize).Select(j => j.Id).ToListAsync(ct);
        foreach (var id in analyteIds)
        {
            var claimed = await db.LabAnalyteEnrichmentJobs
                .Where(j => j.Id == id && j.Status == EnrichmentJobStatus.Deferred)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, EnrichmentJobStatus.Pending)
                    .SetProperty(j => j.Error, (string?)null), ct);
            if (claimed == 1)
                backgroundJobs.Enqueue<LabAnalyteEnrichmentProcessor>(p => p.RunAsync(id, CancellationToken.None));
        }

        var visitMedicationIds = await db.VisitMedicationEnrichmentJobs.AsNoTracking()
            .Where(j => j.Status == EnrichmentJobStatus.Deferred)
            .OrderBy(j => j.CreatedAt).Take(BatchSize).Select(j => j.Id).ToListAsync(ct);
        foreach (var id in visitMedicationIds)
        {
            var claimed = await db.VisitMedicationEnrichmentJobs
                .Where(j => j.Id == id && j.Status == EnrichmentJobStatus.Deferred)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, EnrichmentJobStatus.Pending)
                    .SetProperty(j => j.Error, (string?)null), ct);
            if (claimed == 1)
                backgroundJobs.Enqueue<VisitMedicationEnrichmentProcessor>(p => p.RunAsync(id, CancellationToken.None));
        }

        // Приостановленный прогон прогрева (см. SearchCacheWarmupJob) — не Deferred-задача
        // (это отдельная сущность), но та же природа "паузы вентилем", возобновляется тем же
        // идемпотентным приёмом: Paused -> Running условно, энкью только на реальном захвате.
        var pausedRunId = await db.SearchWarmupRuns.AsNoTracking()
            .Where(r => r.Status == SearchWarmupStatus.Paused)
            .Select(r => r.Id).FirstOrDefaultAsync(ct);
        if (pausedRunId != Guid.Empty)
        {
            var claimed = await db.SearchWarmupRuns
                .Where(r => r.Id == pausedRunId && r.Status == SearchWarmupStatus.Paused)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, SearchWarmupStatus.Running), ct);
            if (claimed == 1)
                backgroundJobs.Enqueue<SearchCacheWarmupJob>(j => j.RunAsync(pausedRunId, CancellationToken.None));
        }

        var totalClaimed = medicationIds.Count + analyteIds.Count + visitMedicationIds.Count;
        if (totalClaimed == BatchSize * 3)
        {
            // Полный батч по каждому конвейеру — вероятно, остались ещё отложенные задачи,
            // самопродолжение (тот же приём, что LabAnalyteKbReenrichJob).
            backgroundJobs.Enqueue<DeferredEnrichmentReleaseJob>(j => j.RunAsync(CancellationToken.None));
        }

        logger.LogInformation(
            "DeferredEnrichmentReleaseJob: возобновлено медикаментов {Medications}, показателей {Analytes}, " +
            "из заключений врача {VisitMedications}{ResumedRun}.",
            medicationIds.Count, analyteIds.Count, visitMedicationIds.Count,
            pausedRunId != Guid.Empty ? $", прогон прогрева {pausedRunId}" : string.Empty);
    }
}
