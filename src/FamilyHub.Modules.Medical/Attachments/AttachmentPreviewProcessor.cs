using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Previews;
using FamilyHub.Infrastructure.Security;
using FamilyHub.Infrastructure.Storage;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Attachments;

/// <summary>
/// Фоновая генерация превью — очередь "previews" (WorkerCount=2, см. Program.cs), ставится в
/// очередь сразу после успешной загрузки вложения (AttachmentService.UploadForMedicalRecordAsync).
/// В отличие от MedicalDocumentExtractionProcessor (одна задача — все ещё не распознанные
/// вложения записи), здесь задача per-attachment: превью не зависит от прочих файлов записи и
/// не имеет смысла батчевать.
///
/// AutomaticRetry — конвертация через внешний сайдкар (Gotenberg) может временно не отвечать
/// (перезапуск/деплой), два повторения Hangfire дешевле, чем сразу показывать пользователю
/// «Не удалось» из-за гонки при деплое.
/// </summary>
[Queue("previews")]
[AutomaticRetry(Attempts = 2)]
public class AttachmentPreviewProcessor(
    AppDbContext db,
    AttachmentService attachments,
    AttachmentPreviewRenderer renderer,
    IFileStorage storage,
    IFileCipher fileCipher,
    IEncryptionKeyRing keyRing,
    ILogger<AttachmentPreviewProcessor> logger)
{
    public async Task RunAsync(Guid attachmentId, CancellationToken ct = default)
    {
        var attachment = await db.FileAttachments.FirstOrDefaultAsync(a => a.Id == attachmentId, ct);
        if (attachment is null)
        {
            logger.LogDebug("Превью: вложение {AttachmentId} уже удалено, пропускаем.", attachmentId);
            return;
        }

        var download = await attachments.GetDownloadAsync(attachmentId, ct);
        if (download is null)
        {
            attachment.PreviewStatus = AttachmentPreviewStatus.Failed;
            attachment.PreviewFailureReason = "Файл не найден в хранилище.";
            attachment.PreviewGeneratedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }

        byte[] content;
        await using (download.Value.Content)
        {
            using var buffer = new MemoryStream();
            await download.Value.Content.CopyToAsync(buffer, ct);
            content = buffer.ToArray();
        }

        var result = await renderer.RenderAsync(content, attachment.ContentType, ct);

        // Переген (после Failed, вручную из админки) не должен копить старые блобы/строки.
        await ReplaceArtifactsAsync(attachmentId, result, ct);

        attachment.PreviewStatus = result.Outcome switch
        {
            PreviewRenderOutcome.Ready => AttachmentPreviewStatus.Ready,
            PreviewRenderOutcome.Unsupported => AttachmentPreviewStatus.Unsupported,
            _ => AttachmentPreviewStatus.Failed,
        };
        attachment.PreviewFailureReason = result.Outcome == PreviewRenderOutcome.Ready ? null : result.FailureReason;
        attachment.PreviewGeneratedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Превью вложения {AttachmentId} готово: {Status} ({ArtifactCount} артефактов).",
            attachmentId, attachment.PreviewStatus, result.Artifacts.Count);
    }

    private async Task ReplaceArtifactsAsync(Guid attachmentId, PreviewRenderResult result, CancellationToken ct)
    {
        var previousKeys = await db.AttachmentPreviews.AsNoTracking()
            .Where(p => p.AttachmentId == attachmentId)
            .Select(p => p.StorageKey)
            .ToListAsync(ct);
        if (previousKeys.Count > 0)
            await db.AttachmentPreviews.Where(p => p.AttachmentId == attachmentId).ExecuteDeleteAsync(ct);

        foreach (var artifact in result.Artifacts)
        {
            var previewId = Guid.NewGuid();
            var storageKey = StorageKeyFactory.CreatePreviewKey(previewId);

            using var plain = new MemoryStream(artifact.Bytes);
            using var encrypted = new MemoryStream();
            var encryptedSize = await fileCipher.EncryptAsync(plain, encrypted, ct);
            encrypted.Position = 0;
            await storage.SaveAsync(storageKey, encrypted, encryptedSize, "application/octet-stream", ct);

            db.AttachmentPreviews.Add(new AttachmentPreview
            {
                Id = previewId,
                AttachmentId = attachmentId,
                Kind = artifact.Kind,
                StorageKey = storageKey,
                ContentType = artifact.ContentType,
                SizeBytes = artifact.Bytes.LongLength,
                Width = artifact.Width,
                Height = artifact.Height,
                PageCount = artifact.PageCount,
                IsEncrypted = true,
                KeyId = keyRing.ActiveKeyId,
                CreatedAt = DateTime.UtcNow,
            });
        }

        // Блобы старых артефактов удаляем best-effort ПОСЛЕ того, как новые строки закоммичены
        // выше (ExecuteDeleteAsync уже сходил в БД) — тот же порядок "строки → блобы", что в
        // MedicalRecordService.DeleteAsync, отсутствие блоба не должно ронять задачу.
        foreach (var key in previousKeys)
        {
            try
            {
                await storage.DeleteAsync(key, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Не удалось удалить устаревший блоб превью {StorageKey}.", key);
            }
        }
    }
}
