using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Audit;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Security;
using FamilyHub.Infrastructure.Storage;
using FamilyHub.Modules.Medical.MedicalRecords;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Attachments;

/// <summary>
/// Метаданные сканов в БД, файлы — в объектном хранилище (раздел 5/9 брифа).
/// Вложение не имеет своей видимости — доступ наследуется от родительской записи
/// (MedicalRecord или Medication). С этапа 2 (152-ФЗ): блоб шифруется IFileCipher перед
/// записью, ключ хранилища не содержит имени файла, имя файла не пишется в логи —
/// в них только идентификаторы.
/// </summary>
public class AttachmentService(
    AppDbContext db,
    IFileStorage storage,
    IFileCipher fileCipher,
    DownloadTokenService downloadTokens,
    MedicalRecordService medicalRecords,
    IFamilyAccessService familyAccess,
    IMedicalAuditWriter audit,
    ILogger<AttachmentService> logger)
{
    /// <summary>Явный лимит вместо неявного дефолта Kestrel (см. аудит
    /// module-review-2026-08-02/03-medical-records-attachments.md, находка 2).</summary>
    public const long MaxSizeBytes = 30 * 1024 * 1024;

    /// <summary>Сканы мед-документов: изображения + PDF. Defense-in-depth — проверка по
    /// заявленному ContentType, не по magic bytes (тело всё равно скачивается с
    /// Content-Disposition: attachment, см. AttachmentEndpoints — снижает риск даже при
    /// подделке заголовка).</summary>
    public static readonly IReadOnlySet<string> AllowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "image/heic", "application/pdf",
    };

    /// <summary>Прикладывать сканы к анализу может только владелец записи — тот же барьер, что и для шаринга.</summary>
    public async Task<(AttachmentAccessResult Result, AttachmentDto? Item)> UploadForMedicalRecordAsync(
        Guid recordId, Guid ownerUserId, string fileName, string contentType, long sizeBytes, Stream content, CancellationToken ct = default)
    {
        if (sizeBytes > MaxSizeBytes)
        {
            logger.LogWarning(
                "Загрузка вложения к мед-записи {RecordId} отклонена: {SizeBytes} байт превышает лимит {MaxSizeBytes}",
                recordId, sizeBytes, MaxSizeBytes);
            return (AttachmentAccessResult.TooLarge, null);
        }
        if (!AllowedContentTypes.Contains(contentType))
        {
            logger.LogWarning(
                "Загрузка вложения к мед-записи {RecordId} отклонена: ContentType {ContentType} не в allow-list",
                recordId, contentType);
            return (AttachmentAccessResult.UnsupportedContentType, null);
        }

        var record = await db.MedicalRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recordId, ct);
        if (record is null)
        {
            logger.LogWarning("Загрузка вложения: мед-запись {RecordId} не найдена (запросил {UserId})", recordId, ownerUserId);
            return (AttachmentAccessResult.NotFound, null);
        }
        if (record.OwnerUserId != ownerUserId)
        {
            logger.LogWarning(
                "Загрузка вложения к мед-записи {RecordId} отклонена: {UserId} не владелец", recordId, ownerUserId);
            return (AttachmentAccessResult.Forbidden, null);
        }

        fileName = FileNameSanitizer.Sanitize(fileName);
        var attachmentId = Guid.NewGuid();
        // Ключ полностью непрозрачен (StorageKeyFactory): не только без имени файла, но и без
        // recordId и без вида родителя — администратор хранилища не видит ни ФИО/диагноз, ни то,
        // что несколько объектов относятся к одной записи (то есть к одному человеку).
        var storageKey = StorageKeyFactory.Create(attachmentId);

        // Шифруем блоб целиком до записи: в хранилище попадает только шифротекст.
        using var encrypted = new MemoryStream();
        var encryptedSize = await fileCipher.EncryptAsync(content, encrypted, ct);
        encrypted.Position = 0;

        logger.LogDebug(
            "Загрузка вложения {AttachmentId} ({SizeBytes} байт, {ContentType}) в хранилище: {StorageKey}",
            attachmentId, sizeBytes, contentType, storageKey);
        await storage.SaveAsync(storageKey, encrypted, encryptedSize, "application/octet-stream", ct);

        var attachment = new FileAttachment
        {
            Id = attachmentId,
            OwnerType = FileOwnerType.MedicalRecord,
            OwnerId = recordId,
            StorageKey = storageKey,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            IsEncrypted = true,
            UploadedAt = DateTime.UtcNow,
        };
        db.FileAttachments.Add(attachment);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Вложение {AttachmentId} добавлено к мед-записи {RecordId} пользователем {UserId}",
            attachmentId, recordId, ownerUserId);
        return (AttachmentAccessResult.Success, ToDto(attachment));
    }

    /// <summary>
    /// Список вложений мед-записи (TECH_DEBT #1: раньше такого эндпоинта не было — фронт держал
    /// вложения только в памяти сессии, они пропадали при перезагрузке страницы). Доступ — тот же
    /// инвариант видимости, что и у самой записи (MedicalRecordService.IsVisibleToAsync), а не
    /// проверка владения: расшаренная запись должна показывать свои сканы и не-владельцу.
    /// </summary>
    public async Task<(AttachmentAccessResult Result, List<AttachmentDto> Items)> GetForMedicalRecordAsync(
        Guid recordId, Guid userId, CancellationToken ct = default)
    {
        var record = await db.MedicalRecords.AsNoTracking()
            .Where(r => r.Id == recordId)
            .Select(r => new { r.Id, r.OwnerUserId })
            .FirstOrDefaultAsync(ct);
        if (record is null)
            return (AttachmentAccessResult.NotFound, []);

        if (!await medicalRecords.IsVisibleToAsync(recordId, userId, ct))
        {
            logger.LogWarning(
                "Список вложений мед-записи {RecordId} отклонён: {UserId} нет доступа", recordId, userId);
            return (AttachmentAccessResult.Forbidden, []);
        }

        // Просмотр списка вложений ЧУЖОЙ (расшаренной) записи — тоже факт доступа к чужим
        // медданным (тот же приём, что MedicalRecordService.GetVisibleRecordsAsync).
        if (record.OwnerUserId != userId)
            await audit.WriteAsync(userId, MedicalAccessAction.ViewList, ownerUserId: record.OwnerUserId, medicalRecordId: recordId, ct: ct);

        var items = await db.FileAttachments.AsNoTracking()
            .Where(a => a.OwnerType == FileOwnerType.MedicalRecord && a.OwnerId == recordId)
            .OrderBy(a => a.UploadedAt)
            .ToListAsync(ct);

        return (AttachmentAccessResult.Success, items.Select(ToDto).ToList());
    }

    /// <summary>
    /// Короткоживущая ссылка на скачивание (наш API-эндпоинт с расшифровкой), после
    /// проверки доступа к родительской записи. Авторизация — здесь, в момент выдачи.
    /// </summary>
    public async Task<(AttachmentAccessResult Result, string? Url)> GetPresignedUrlAsync(
        Guid attachmentId, Guid userId, CancellationToken ct = default)
    {
        var attachment = await db.FileAttachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == attachmentId, ct);
        if (attachment is null)
        {
            logger.LogWarning("Ссылка на вложение {AttachmentId}: не найдено (запросил {UserId})", attachmentId, userId);
            return (AttachmentAccessResult.NotFound, null);
        }

        var hasAccess = attachment.OwnerType switch
        {
            FileOwnerType.MedicalRecord => await medicalRecords.IsVisibleToAsync(attachment.OwnerId, userId, ct),
            FileOwnerType.Medication => await HasMedicationAccessAsync(attachment.OwnerId, userId, ct),
            _ => false,
        };
        if (!hasAccess)
        {
            logger.LogWarning(
                "Ссылка на вложение {AttachmentId} отклонена: {UserId} нет доступа к {OwnerType} {OwnerId}",
                attachmentId, userId, attachment.OwnerType, attachment.OwnerId);
            return (AttachmentAccessResult.Forbidden, null);
        }

        // Аудит (задача 2.7) — в момент выдачи ссылки: это и есть момент авторизации доступа
        // к файлу (сам download-эндпоинт проверяет только подпись токена).
        var ownerUserId = attachment.OwnerType == FileOwnerType.MedicalRecord
            ? await db.MedicalRecords.AsNoTracking()
                .Where(r => r.Id == attachment.OwnerId).Select(r => (Guid?)r.OwnerUserId).FirstOrDefaultAsync(ct)
            : null;
        await audit.WriteAsync(
            userId, MedicalAccessAction.DownloadAttachment,
            ownerUserId: ownerUserId,
            medicalRecordId: attachment.OwnerType == FileOwnerType.MedicalRecord ? attachment.OwnerId : null,
            attachmentId: attachmentId, ct: ct);

        var url = downloadTokens.CreateUrl(attachmentId);
        logger.LogDebug("Выдана ссылка на скачивание вложения {AttachmentId} пользователю {UserId}", attachmentId, userId);
        return (AttachmentAccessResult.Success, url);
    }

    /// <summary>
    /// Отдаёт содержимое вложения (расшифрованное для IsEncrypted, как есть — для legacy).
    /// Авторизация уже произошла при выдаче подписанной ссылки (см. GetPresignedUrlAsync).
    /// </summary>
    public async Task<(Stream Content, string ContentType, string FileName)?> GetDownloadAsync(
        Guid attachmentId, CancellationToken ct = default)
    {
        var attachment = await db.FileAttachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == attachmentId, ct);
        if (attachment is null) return null;

        var stored = await storage.OpenReadAsync(attachment.StorageKey, ct);
        if (!attachment.IsEncrypted)
            return (stored, attachment.ContentType, attachment.FileName);

        await using (stored)
        {
            var plain = await fileCipher.DecryptAsync(stored, ct);
            return (plain, attachment.ContentType, attachment.FileName);
        }
    }

    private async Task<bool> HasMedicationAccessAsync(Guid medicationId, Guid userId, CancellationToken ct)
    {
        var familyId = await db.Medications.AsNoTracking()
            .Where(m => m.Id == medicationId)
            .Select(m => m.FamilyId)
            .FirstOrDefaultAsync(ct);

        return familyId != Guid.Empty && await familyAccess.HasRoleAsync(userId, familyId, FamilyRole.Member, ct);
    }

    private static AttachmentDto ToDto(FileAttachment a) =>
        new(a.Id, a.FileName, a.ContentType, a.SizeBytes, a.UploadedAt);
}
