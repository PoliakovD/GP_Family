using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.Security;
using FamilyHub.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Chat.Chat;

/// <summary>
/// Блобы вложений чата (ADR-0019 п. 6): то же шифрование и непрозрачные ключи, что у мед-вложений
/// (AttachmentService.UploadRawAsync в Medical), но без превью-конвейера — картинки и PDF вьюер рисует из
/// оригинала (PreviewStatus=Ready), остальное — только скачивание. Просмотр идёт существующими
/// /api/attachments/{id}/... — доступ там проверяет ветка FileOwnerType.ChatMessage.
/// </summary>
public class ChatAttachmentStore(
    IFileStorage storage,
    IFileCipher fileCipher,
    IEncryptionKeyRing keyRing,
    ILogger<ChatAttachmentStore> logger)
{
    /// <summary>Что браузер нарисует из оригинала сам (HEIC/TIFF — нет, им нужен Medical-конвейер нормализации).</summary>
    private static readonly IReadOnlySet<string> BrowserRenderable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        DocumentContentTypes.Jpeg, DocumentContentTypes.Png, DocumentContentTypes.Webp, DocumentContentTypes.Pdf,
    };

    /// <summary>Шифрует и кладёт блоб; строку FileAttachment возвращает несохранённой — вызывающий
    /// добавляет её в контекст вместе с самим сообщением, одним SaveChanges.</summary>
    public async Task<FileAttachment> StoreAsync(Guid messageId, ChatUpload upload, CancellationToken ct = default)
    {
        var attachmentId = Guid.NewGuid();
        var storageKey = StorageKeyFactory.Create(attachmentId);

        await using (var content = upload.OpenRead())
        {
            using var encrypted = new MemoryStream();
            var encryptedSize = await fileCipher.EncryptAsync(content, encrypted, ct);
            encrypted.Position = 0;
            await storage.SaveAsync(storageKey, encrypted, encryptedSize, "application/octet-stream", ct);
        }

        var renderable = BrowserRenderable.Contains(upload.ContentType);
        return new FileAttachment
        {
            Id = attachmentId,
            OwnerType = FileOwnerType.ChatMessage,
            OwnerId = messageId,
            StorageKey = storageKey,
            FileName = FileNameSanitizer.Sanitize(upload.FileName),
            ContentType = upload.ContentType,
            SizeBytes = upload.SizeBytes,
            IsEncrypted = true,
            KeyId = keyRing.ActiveKeyId,
            UploadedAt = DateTime.UtcNow,
            PreviewStatus = renderable ? AttachmentPreviewStatus.Ready : AttachmentPreviewStatus.Unsupported,
        };
    }

    /// <summary>Удаление блобов — после коммита БД и best-effort: осиротевший шифрованный блоб нечитаем без
    /// строки FileAttachment (тот же принцип, что в AccountService).</summary>
    public async Task DeleteBlobsAsync(IEnumerable<string> storageKeys, CancellationToken ct = default)
    {
        foreach (var key in storageKeys)
        {
            try
            {
                await storage.DeleteAsync(key, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Чат: не удалось удалить блоб {StorageKey} — требуется ручная зачистка", key);
            }
        }
    }
}
