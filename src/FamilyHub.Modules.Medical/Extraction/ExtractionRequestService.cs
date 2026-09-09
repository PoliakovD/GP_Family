using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Persistence;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary><see cref="ServiceUnavailable"/> — последняя задача записи упала технически (LM
/// Studio был недоступен) и сервер всё ещё недоступен прямо сейчас (см. класс-doc
/// ExtractionRequestService) — отличается от <see cref="AlreadyQueued"/> (задача уже реально
/// исполняется/ждёт) тем, что новая задача здесь СОЗНАТЕЛЬНО не создаётся: она немедленно упала
/// бы тем же образом, а исходную задачу уже вернёт в очередь LmStudioRecoverySweepJob, как только
/// сервер оживёт.</summary>
public enum ExtractionRequestResult { Success, NotFound, Forbidden, AlreadyQueued, NothingToDo, ServiceUnavailable }

/// <summary>
/// Постановка ЗАПИСИ в очередь распознавания (ветка medicalrecords, редизайн v2 — раньше был
/// per-attachment: за один клик обрабатывается ОДНО вложение, теперь одна кнопка «Распознать»
/// обрабатывает все ещё не распознанные вложения записи последовательно, см.
/// MedicalDocumentExtractionProcessor). По образцу EnrichmentRequestService.EnqueueAsync (этап 4):
/// Pending-строка и Hangfire-энкью в одной явной транзакции, дедуп через частичный уникальный
/// индекс по MedicalRecordId + catch DbUpdateException. Вызывается только владельцем записи (см.
/// ExtractionEndpoints) — тот же барьер, что и для загрузки/шаринга вложений.
/// </summary>
public class ExtractionRequestService(
    AppDbContext db,
    IBackgroundJobClient backgroundJobs,
    ILmStudioAvailabilityProbe probe,
    ILogger<ExtractionRequestService> logger)
{
    public async Task<ExtractionRequestResult> RequestAsync(
        Guid recordId, Guid requestedByUserId, CancellationToken ct = default)
    {
        var record = await db.MedicalRecords.AsNoTracking()
            .Where(r => r.Id == recordId)
            .Select(r => new { r.OwnerUserId })
            .FirstOrDefaultAsync(ct);
        if (record is null) return ExtractionRequestResult.NotFound;
        if (record.OwnerUserId != requestedByUserId) return ExtractionRequestResult.Forbidden;

        // Явная предпроверка живой задачи — вместо того, чтобы узнавать о дубле только постфактум
        // по DbUpdateException на уникальном индексе ниже (та ловушка остаётся как подстраховка от
        // гонки, см. catch). Даёт вернуть понятный AlreadyQueued без лишнего похода в транзакцию.
        var hasLiveJob = await db.MedicalDocumentExtractionJobs.AsNoTracking()
            .AnyAsync(j => j.MedicalRecordId == recordId &&
                (j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running), ct);
        if (hasLiveJob) return ExtractionRequestResult.AlreadyQueued;

        var hasPendingAttachments = await db.FileAttachments.AsNoTracking()
            .AnyAsync(a => a.OwnerType == FileOwnerType.MedicalRecord && a.OwnerId == recordId && a.ExtractedAt == null, ct);
        if (!hasPendingAttachments) return ExtractionRequestResult.NothingToDo;

        // Последняя задача этой записи упала технически, и сервер всё ещё недоступен прямо сейчас —
        // новая задача упала бы тем же образом немедленно; сообщаем понятно вместо того, чтобы
        // тратить попытку впустую. LmStudioRecoverySweepJob сам вернёт исходную задачу в очередь,
        // как только сервер снова ответит (см. план, часть 1).
        var lastJob = await db.MedicalDocumentExtractionJobs.AsNoTracking()
            .Where(j => j.MedicalRecordId == recordId)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (lastJob is { Status: EnrichmentJobStatus.Failed, IsTransientFailure: true } && !await probe.IsAvailableAsync(ct))
            return ExtractionRequestResult.ServiceUnavailable;

        var job = new MedicalDocumentExtractionJob
        {
            Id = Guid.NewGuid(),
            MedicalRecordId = recordId,
            RequestedByUserId = requestedByUserId,
            Status = EnrichmentJobStatus.Pending,
            Stage = ExtractionStage.Queued,
            CreatedAt = DateTime.UtcNow,
        };
        db.MedicalDocumentExtractionJobs.Add(job);

        // Та же причина явной транзакции, что в EnrichmentRequestService.EnqueueAsync: Hangfire
        // использует отдельное соединение, сбой энкью не должен оставлять "висячую" Pending-строку,
        // которая навсегда заблокирует дедупом повторные попытки для этой записи.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
            backgroundJobs.Enqueue<MedicalDocumentExtractionProcessor>(p => p.RunAsync(job.Id, CancellationToken.None));
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            await tx.RollbackAsync(ct);
            logger.LogDebug(ex, "Распознавание мед-записи {RecordId} уже в очереди, пропускаем.", recordId);
            db.Entry(job).State = EntityState.Detached;
            return ExtractionRequestResult.AlreadyQueued;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            logger.LogWarning(ex, "Не удалось поставить распознавание мед-записи {RecordId} в очередь.", recordId);
            db.Entry(job).State = EntityState.Detached;
            throw;
        }

        logger.LogInformation("Распознавание мед-записи {RecordId} поставлено в очередь.", recordId);
        return ExtractionRequestResult.Success;
    }
}
