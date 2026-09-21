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
///
/// Все четыре конвейера участвуют в свипе (VisitMedicationEnrichmentJob получила колонку
/// IsTransientFailure при cleanup-рефакторинге — раньше её не было, это был структурный пробел,
/// не сознательный пропуск). Дедуп-проверка "нет ли уже живой задачи" у каждого конвейера свой
/// ключ (MedicalRecordId у extraction, NormalizedName+SpecimenKbId у lab-analyte, NormalizedName
/// у medication/visit-medication) — не унифицирована в общий метод намеренно, ResetForRetry
/// (то, что реально совпадает у всех четырёх) вынесен в один generic-метод через IPipelineJob.
///
/// [Queue("extraction")] — сама задача не зовёт LM Studio напрямую (только probe.IsAvailableAsync
/// + чтение/запись БД + постановка ДРУГИХ задач в очередь), поэтому не обязана жить на "default";
/// логически принадлежит конвейеру распознавания, которым и управляет в первую очередь.
/// </summary>
[Queue("extraction")]
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
        var visitMedication = await RequeueVisitMedicationJobsAsync(cutoff, ct);

        if (extraction + labAnalyte + medication + visitMedication > 0)
        {
            logger.LogInformation(
                "LmStudioRecoverySweepJob: сервер снова доступен, возвращено в очередь — " +
                "{Extraction} распознаваний, {LabAnalyte} обогащений показателей, {Medication} обогащений " +
                "препаратов, {VisitMedication} обогащений из заключений врача.",
                extraction, labAnalyte, medication, visitMedication);
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

    private async Task<int> RequeueVisitMedicationJobsAsync(DateTime cutoff, CancellationToken ct)
    {
        var candidates = await db.VisitMedicationEnrichmentJobs
            .Where(j => j.Status == EnrichmentJobStatus.Failed && j.IsTransientFailure && j.CreatedAt > cutoff)
            .ToListAsync(ct);

        var requeued = 0;
        foreach (var job in candidates)
        {
            // Deferred тоже "живая" — та же строка под тем же дедуп-индексом (Status IN (0,1,5)),
            // просто ждёт открытия вентиля платного поиска (ADR-0005 §9).
            var hasLiveJob = await db.VisitMedicationEnrichmentJobs.AnyAsync(j =>
                j.Id != job.Id && j.NormalizedName == job.NormalizedName &&
                (j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running
                    || j.Status == EnrichmentJobStatus.Deferred), ct);
            if (hasLiveJob) continue;

            ResetForRetry(job);
            await db.SaveChangesAsync(ct);
            backgroundJobs.Enqueue<VisitMedicationEnrichmentProcessor>(p => p.RunAsync(job.Id, CancellationToken.None));
            requeued++;
        }
        return requeued;
    }

    /// <summary>Общее для всех четырёх конвейеров через IPipelineJob — раньше было три
    /// почти идентичные перегрузки (по одной на каждый тип, без VisitMedication — у неё до
    /// cleanup-рефакторинга не было даже поля IsTransientFailure).
    /// MedicalDocumentExtractionJob.Stage — единственное специфичное для extraction поле вне
    /// общего интерфейса, сбрасывается отдельным условием ниже, а не отдельным методом.</summary>
    private static void ResetForRetry(IPipelineJob job)
    {
        job.Attempts = 0;
        job.Status = EnrichmentJobStatus.Pending;
        job.Error = null;
        job.IsTransientFailure = false;
        job.StartedAt = null;
        job.CompletedAt = null;
        if (job is MedicalDocumentExtractionJob extraction)
            extraction.Stage = ExtractionStage.Queued;
    }
}
