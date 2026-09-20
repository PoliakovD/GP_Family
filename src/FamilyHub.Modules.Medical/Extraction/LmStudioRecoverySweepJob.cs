using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Enrichment;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Рекуррентный досып задач, упавших ТЕХНИЧЕСКИ (LM Studio был недоступен — ноутбук выключен/спит,
/// см. IsTransientFailure на трёх job-таблицах) — без этой задачи такой сбой остаётся терминальным
/// Failed навсегда после исчерпания [AutomaticRetry] (расписание [60, 600, 3600] — около часа), а
/// пользователю нужно заново нажимать «Распознать» самому, даже когда сервер уже давно снова
/// доступен. Тот же паттерн "ночной добиватель", что EncryptionRotationJob (см. Program.cs) — не
/// запускает НОВУЮ работу, только резюмирует то, что Hangfire уже бросил после своих ретраев.
///
/// Окно в 7 дней (CreatedAt) — не резюмируем сколь угодно старые сбои: если ноутбук не включали
/// неделю, скорее всего пользователь уже сам разобрался или запись неактуальна; не захламляем
/// очередь бесконечно растущим хвостом.
/// </summary>
public class LmStudioRecoverySweepJob(
    AppDbContext db,
    ILmStudioAvailabilityProbe probe,
    IBackgroundJobClient backgroundJobs,
    ILogger<LmStudioRecoverySweepJob> logger)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (!await probe.IsAvailableAsync(ct))
        {
            logger.LogDebug("LmStudioRecoverySweepJob: сервер всё ещё недоступен, досып пропущен.");
            return;
        }

        var cutoff = DateTime.UtcNow - MaxAge;
        var extraction = await RequeueExtractionJobsAsync(cutoff, ct);
        var labAnalyte = await RequeueLabAnalyteJobsAsync(cutoff, ct);
        var medication = await RequeueMedicationJobsAsync(cutoff, ct);

        if (extraction + labAnalyte + medication > 0)
        {
            logger.LogInformation(
                "LmStudioRecoverySweepJob: сервер снова доступен, возвращено в очередь — " +
                "{Extraction} распознаваний, {LabAnalyte} обогащений показателей, {Medication} обогащений препаратов.",
                extraction, labAnalyte, medication);
        }
    }

    private async Task<int> RequeueExtractionJobsAsync(DateTime cutoff, CancellationToken ct)
    {
        var candidates = await db.MedicalDocumentExtractionJobs
            .Where(j => j.Status == EnrichmentJobStatus.Failed && j.IsTransientFailure && j.CreatedAt > cutoff)
            .ToListAsync(ct);

        var requeued = 0;
        foreach (var job in candidates)
        {
            // Пользователь мог уже сам нажать «Распознать» повторно, пока эта задача ждала свипа —
            // не создаём вторую живую задачу поверх той, что уже в очереди/выполняется.
            var hasLiveJob = await db.MedicalDocumentExtractionJobs.AnyAsync(j =>
                j.Id != job.Id && j.MedicalRecordId == job.MedicalRecordId &&
                (j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running), ct);
            if (hasLiveJob) continue;

            ResetForRetry(job);
            // Запись была помечена Failed терминальным catch в RunAsync — теперь задача снова
            // живая, запись должна опять показывать "в процессе", а не оставаться замороженной на
            // Failed до следующего ручного клика «Распознать».
            await db.MedicalRecords.Where(r => r.Id == job.MedicalRecordId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ExtractionStatus, ExtractionStatus.Pending), ct);
            await db.SaveChangesAsync(ct);
            backgroundJobs.Enqueue<MedicalDocumentExtractionProcessor>(p => p.RunAsync(job.Id, CancellationToken.None));
            requeued++;
        }
        return requeued;
    }

    private async Task<int> RequeueLabAnalyteJobsAsync(DateTime cutoff, CancellationToken ct)
    {
        var candidates = await db.LabAnalyteEnrichmentJobs
            .Where(j => j.Status == EnrichmentJobStatus.Failed && j.IsTransientFailure && j.CreatedAt > cutoff)
            .ToListAsync(ct);

        var requeued = 0;
        foreach (var job in candidates)
        {
            // Deferred тоже "живая" — та же строка под тем же дедуп-индексом (Status IN (0,1,5)),
            // просто ждёт открытия вентиля платного поиска (ADR-0005 §9).
            var hasLiveJob = await db.LabAnalyteEnrichmentJobs.AnyAsync(j =>
                j.Id != job.Id && j.NormalizedName == job.NormalizedName && j.SpecimenKbId == job.SpecimenKbId &&
                (j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running
                    || j.Status == EnrichmentJobStatus.Deferred), ct);
            if (hasLiveJob) continue;

            ResetForRetry(job);
            await db.SaveChangesAsync(ct);
            backgroundJobs.Enqueue<LabAnalyteEnrichmentProcessor>(p => p.RunAsync(job.Id, CancellationToken.None));
            requeued++;
        }
        return requeued;
    }

    private async Task<int> RequeueMedicationJobsAsync(DateTime cutoff, CancellationToken ct)
    {
        var candidates = await db.MedicationEnrichmentJobs
            .Where(j => j.Status == EnrichmentJobStatus.Failed && j.IsTransientFailure && j.CreatedAt > cutoff)
            .ToListAsync(ct);

        var requeued = 0;
        foreach (var job in candidates)
        {
            // Deferred тоже "живая" — та же строка под тем же дедуп-индексом (Status IN (0,1,5)),
            // просто ждёт открытия вентиля платного поиска (ADR-0005 §9).
            var hasLiveJob = await db.MedicationEnrichmentJobs.AnyAsync(j =>
                j.Id != job.Id && j.NormalizedName == job.NormalizedName &&
                (j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running
                    || j.Status == EnrichmentJobStatus.Deferred), ct);
            if (hasLiveJob) continue;

            ResetForRetry(job);
            await db.SaveChangesAsync(ct);
            backgroundJobs.Enqueue<MedicationEnrichmentProcessor>(p => p.RunAsync(job.Id, CancellationToken.None));
            requeued++;
        }
        return requeued;
    }

    private static void ResetForRetry(MedicalDocumentExtractionJob job)
    {
        job.Attempts = 0;
        job.Status = EnrichmentJobStatus.Pending;
        job.Stage = ExtractionStage.Queued;
        job.Error = null;
        job.IsTransientFailure = false;
        job.StartedAt = null;
        job.CompletedAt = null;
    }

    private static void ResetForRetry(LabAnalyteEnrichmentJob job)
    {
        job.Attempts = 0;
        job.Status = EnrichmentJobStatus.Pending;
        job.Error = null;
        job.IsTransientFailure = false;
        job.StartedAt = null;
        job.CompletedAt = null;
    }

    private static void ResetForRetry(MedicationEnrichmentJob job)
    {
        job.Attempts = 0;
        job.Status = EnrichmentJobStatus.Pending;
        job.Error = null;
        job.IsTransientFailure = false;
        job.StartedAt = null;
        job.CompletedAt = null;
    }
}
