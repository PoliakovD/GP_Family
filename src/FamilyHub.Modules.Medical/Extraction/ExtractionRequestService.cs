using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Extraction;

public enum ExtractionRequestResult { Success, NotFound, Forbidden, AlreadyQueued }

/// <summary>
/// Постановка вложения в очередь распознавания (ветка medicalrecords) — по образцу
/// EnrichmentRequestService.EnqueueAsync (этап 4): Pending-строка и Hangfire-энкью в одной явной
/// транзакции, дедуп через частичный уникальный индекс + catch DbUpdateException. Вызывается
/// только владельцем записи (см. MedicalDocumentExtractionEndpoints) — тот же барьер, что и для
/// загрузки/шаринга вложений.
/// </summary>
public class ExtractionRequestService(
    AppDbContext db,
    IBackgroundJobClient backgroundJobs,
    ILogger<ExtractionRequestService> logger)
{
    public async Task<ExtractionRequestResult> RequestAsync(
        Guid recordId, Guid attachmentId, Guid requestedByUserId, CancellationToken ct = default)
    {
        var record = await db.MedicalRecords.AsNoTracking()
            .Where(r => r.Id == recordId)
            .Select(r => new { r.OwnerUserId })
            .FirstOrDefaultAsync(ct);
        if (record is null) return ExtractionRequestResult.NotFound;
        if (record.OwnerUserId != requestedByUserId) return ExtractionRequestResult.Forbidden;

        var attachmentExists = await db.FileAttachments.AsNoTracking()
            .AnyAsync(a => a.Id == attachmentId && a.OwnerType == FileOwnerType.MedicalRecord && a.OwnerId == recordId, ct);
        if (!attachmentExists) return ExtractionRequestResult.NotFound;

        var job = new MedicalDocumentExtractionJob
        {
            Id = Guid.NewGuid(),
            MedicalRecordId = recordId,
            AttachmentId = attachmentId,
            RequestedByUserId = requestedByUserId,
            Status = EnrichmentJobStatus.Pending,
            Stage = ExtractionStage.Queued,
            CreatedAt = DateTime.UtcNow,
        };
        db.MedicalDocumentExtractionJobs.Add(job);

        // Та же причина явной транзакции, что в EnrichmentRequestService.EnqueueAsync: Hangfire
        // использует отдельное соединение, сбой энкью не должен оставлять "висячую" Pending-строку,
        // которая навсегда заблокирует дедупом повторные попытки для этого вложения.
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
            logger.LogDebug(ex, "Распознавание вложения {AttachmentId} уже в очереди, пропускаем.", attachmentId);
            db.Entry(job).State = EntityState.Detached;
            return ExtractionRequestResult.AlreadyQueued;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            logger.LogWarning(ex, "Не удалось поставить распознавание вложения {AttachmentId} в очередь.", attachmentId);
            db.Entry(job).State = EntityState.Detached;
            throw;
        }

        logger.LogInformation("Распознавание вложения {AttachmentId} мед-записи {RecordId} поставлено в очередь.", attachmentId, recordId);
        return ExtractionRequestResult.Success;
    }
}
