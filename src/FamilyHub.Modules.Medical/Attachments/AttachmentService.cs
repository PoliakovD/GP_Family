using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Audit;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Security;
using FamilyHub.Infrastructure.Storage;
using FamilyHub.Modules.Medical.MedicalRecords;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    IEncryptionKeyRing keyRing,
    DownloadTokenService downloadTokens,
    MedicalRecordService medicalRecords,
    IFamilyAccessService familyAccess,
    IMedicalAuditWriter audit,
    IBackgroundJobClient backgroundJobs,
    IOptions<AttachmentUploadOptions> uploadOptions,
    ILogger<AttachmentService> logger)
{
    /// <summary>Явный лимит вместо неявного дефолта Kestrel (см. аудит
    /// module-review-2026-08-02/03-medical-records-attachments.md, находка 2) — теперь
    /// настраивается через AttachmentUploadOptions (env Attachments__MaxFileSizeBytes).</summary>
    public long MaxSizeBytes => uploadOptions.Value.MaxFileSizeBytes;

    /// <summary>Максимум вложений на одну мед-запись (env Attachments__MaxFilesPerRecord).</summary>
    public int MaxFilesPerRecord => uploadOptions.Value.MaxFilesPerRecord;

    public AttachmentLimitsDto Limits => new(MaxSizeBytes, MaxFilesPerRecord);

    /// <summary>Сканы мед-документов: изображения, PDF, офисные форматы, текст (ветка
    /// medicalrecords расширила список под конвейер извлечения — см.
    /// FamilyHub.Infrastructure.Documents.DocumentContentTypes, единая точка правды и для
    /// допустимой загрузки, и для того, что конвейер умеет распознать). Defense-in-depth —
    /// проверка по заявленному ContentType, не по magic bytes (тело всё равно скачивается с
    /// Content-Disposition: attachment, см. AttachmentEndpoints — снижает риск даже при
    /// подделке заголовка).</summary>
    public static readonly IReadOnlySet<string> AllowedContentTypes = DocumentContentTypes.All;

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

        var existingCount = await db.FileAttachments
            .CountAsync(a => a.OwnerType == FileOwnerType.MedicalRecord && a.OwnerId == recordId, ct);
        if (existingCount >= MaxFilesPerRecord)
        {
            logger.LogWarning(
                "Загрузка вложения к мед-записи {RecordId} отклонена: уже {Count} вложений, лимит {MaxFilesPerRecord}",
                recordId, existingCount, MaxFilesPerRecord);
            return (AttachmentAccessResult.TooManyFiles, null);
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
            KeyId = keyRing.ActiveKeyId,
            UploadedAt = DateTime.UtcNow,
            PreviewStatus = AttachmentPreviewStatus.Pending,
        };
        db.FileAttachments.Add(attachment);
        await db.SaveChangesAsync(ct);

        // Постановка в очередь — best-effort ПОСЛЕ успешного коммита вложения: превью
        // производный артефакт, сбой планировщика не должен откатывать сам факт загрузки файла
        // (в отличие от ExtractionRequestService.RequestAsync, где Pending-строка — единственный
        // смысл операции и без энкью бессмысленна).
        try
        {
            backgroundJobs.Enqueue<AttachmentPreviewProcessor>(p => p.RunAsync(attachment.Id, CancellationToken.None));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Не удалось поставить генерацию превью вложения {AttachmentId} в очередь — предпросмотр будет недоступен.",
                attachment.Id);
            attachment.PreviewStatus = AttachmentPreviewStatus.Failed;
            attachment.PreviewFailureReason = "Не удалось поставить генерацию превью в очередь.";
            await db.SaveChangesAsync(ct);
        }

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

        if (!await HasAccessAsync(attachment, userId, ct))
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

        var url = downloadTokens.CreateUrl(attachmentId, DownloadScope.File);
        logger.LogDebug("Выдана ссылка на скачивание вложения {AttachmentId} пользователю {UserId}", attachmentId, userId);
        return (AttachmentAccessResult.Success, url);
    }

    /// <summary>
    /// Описание превью + уже подписанные ссылки на всё, что вьюеру может понадобиться (миниатюра,
    /// основной контент, скачивание) — фронт не знает про AttachmentPreviewKind/DownloadScope
    /// вовсе, только про готовые URL и AttachmentRenderKind. Легаси-вложения (PreviewStatus=None,
    /// загружены до появления этой функции) получают превью лениво — постановка в очередь прямо
    /// здесь, при первом открытии, вместо отдельного бэкфилл-скрипта по всей таблице.
    /// </summary>
    public async Task<(AttachmentAccessResult Result, AttachmentPreviewDto? Item)> GetPreviewAsync(
        Guid attachmentId, Guid userId, CancellationToken ct = default)
    {
        var attachment = await db.FileAttachments.FirstOrDefaultAsync(a => a.Id == attachmentId, ct);
        if (attachment is null) return (AttachmentAccessResult.NotFound, null);

        if (!await HasAccessAsync(attachment, userId, ct))
            return (AttachmentAccessResult.Forbidden, null);

        if (attachment.PreviewStatus == AttachmentPreviewStatus.None)
        {
            attachment.PreviewStatus = AttachmentPreviewStatus.Pending;
            await db.SaveChangesAsync(ct);
            try
            {
                backgroundJobs.Enqueue<AttachmentPreviewProcessor>(p => p.RunAsync(attachment.Id, CancellationToken.None));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Не удалось поставить (лениво, при открытии) генерацию превью вложения {AttachmentId} в очередь.",
                    attachmentId);
                attachment.PreviewStatus = AttachmentPreviewStatus.Failed;
                attachment.PreviewFailureReason = "Не удалось поставить генерацию превью в очередь.";
                await db.SaveChangesAsync(ct);
            }
        }

        var downloadUrl = downloadTokens.CreateUrl(attachmentId, DownloadScope.File);

        if (attachment.PreviewStatus == AttachmentPreviewStatus.Pending)
        {
            return (AttachmentAccessResult.Success, new AttachmentPreviewDto(
                attachment.PreviewStatus, AttachmentRenderKind.None, attachment.FileName, attachment.ContentType,
                attachment.SizeBytes, null, null, null, downloadUrl, null));
        }

        // text/csv/xml — вьюер тянет исходник напрямую (не заходя за AttachmentPreviews вовсе,
        // для этих форматов артефактов и не генерируется, см. AttachmentPreviewRenderer).
        if (DocumentContentTypes.PlainTextLike.Contains(attachment.ContentType))
        {
            var textUrl = downloadTokens.CreateUrl(attachmentId, DownloadScope.Inline);
            return (AttachmentAccessResult.Success, new AttachmentPreviewDto(
                attachment.PreviewStatus, AttachmentRenderKind.Text, attachment.FileName, attachment.ContentType,
                attachment.SizeBytes, null, null, textUrl, downloadUrl, attachment.PreviewFailureReason));
        }

        if (attachment.PreviewStatus is AttachmentPreviewStatus.Unsupported or AttachmentPreviewStatus.Failed)
        {
            return (AttachmentAccessResult.Success, new AttachmentPreviewDto(
                attachment.PreviewStatus, AttachmentRenderKind.None, attachment.FileName, attachment.ContentType,
                attachment.SizeBytes, null, null, null, downloadUrl, attachment.PreviewFailureReason));
        }

        var artifacts = await db.AttachmentPreviews.AsNoTracking()
            .Where(p => p.AttachmentId == attachmentId)
            .ToListAsync(ct);
        var thumbnail = artifacts.FirstOrDefault(a => a.Kind == AttachmentPreviewKind.Thumbnail);
        var pdfArtifact = artifacts.FirstOrDefault(a => a.Kind == AttachmentPreviewKind.Pdf);
        var pageArtifact = artifacts.FirstOrDefault(a => a.Kind == AttachmentPreviewKind.Page);
        var thumbnailUrl = thumbnail is null ? null : downloadTokens.CreateUrl(attachmentId, DownloadScope.Thumbnail);

        AttachmentRenderKind renderKind;
        string? contentUrl;
        int? pageCount;
        if (pdfArtifact is not null)
        {
            (renderKind, contentUrl, pageCount) =
                (AttachmentRenderKind.Pdf, downloadTokens.CreateUrl(attachmentId, DownloadScope.PreviewPdf), pdfArtifact.PageCount);
        }
        else if (attachment.ContentType.Equals(DocumentContentTypes.Pdf, StringComparison.OrdinalIgnoreCase))
        {
            // Оригинал уже PDF — вьюер рисует его напрямую, отдельного PDF-артефакта не было и не нужно.
            (renderKind, contentUrl, pageCount) =
                (AttachmentRenderKind.Pdf, downloadTokens.CreateUrl(attachmentId, DownloadScope.Inline), thumbnail?.PageCount);
        }
        else if (pageArtifact is not null)
        {
            // HEIC/TIFF — браузер сам не отрисует, показываем нормализованную страницу.
            (renderKind, contentUrl, pageCount) =
                (AttachmentRenderKind.Image, downloadTokens.CreateUrl(attachmentId, DownloadScope.PreviewPage), null);
        }
        else if (DocumentContentTypes.Images.Contains(attachment.ContentType))
        {
            // jpeg/png/webp — браузер отрисует оригинал сам, без нормализации.
            (renderKind, contentUrl, pageCount) =
                (AttachmentRenderKind.Image, downloadTokens.CreateUrl(attachmentId, DownloadScope.Inline), null);
        }
        else
        {
            (renderKind, contentUrl, pageCount) = (AttachmentRenderKind.None, null, null);
        }

        return (AttachmentAccessResult.Success, new AttachmentPreviewDto(
            attachment.PreviewStatus, renderKind, attachment.FileName, attachment.ContentType, attachment.SizeBytes,
            pageCount, thumbnailUrl, contentUrl, downloadUrl, null));
    }

    /// <summary>
    /// Отдаёт содержимое вложения (расшифрованное для IsEncrypted, как есть — для legacy).
    /// Авторизация уже произошла при выдаче подписанной ссылки (см. GetPresignedUrlAsync/GetPreviewAsync).
    /// Обслуживает и /file (скачивание), и /inline (отрисовка исходника — PDF/картинка/текст) —
    /// разница только в заголовке Content-Disposition, который проставляет сам эндпоинт.
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

    /// <summary>Отдаёт один превью-артефакт (миниатюра/PDF/нормализованная страница) — та же
    /// расшифровка, что и GetDownloadAsync, только источник AttachmentPreviews, не FileAttachments.
    /// Авторизация — уже произошла при выдаче подписанной ссылки (GetPreviewAsync).</summary>
    public async Task<(Stream Content, string ContentType)?> GetPreviewArtifactAsync(
        Guid attachmentId, AttachmentPreviewKind kind, CancellationToken ct = default)
    {
        var artifact = await db.AttachmentPreviews.AsNoTracking()
            .FirstOrDefaultAsync(p => p.AttachmentId == attachmentId && p.Kind == kind, ct);
        if (artifact is null) return null;

        var stored = await storage.OpenReadAsync(artifact.StorageKey, ct);
        if (!artifact.IsEncrypted)
            return (stored, artifact.ContentType);

        await using (stored)
        {
            var plain = await fileCipher.DecryptAsync(stored, ct);
            return (plain, artifact.ContentType);
        }
    }

    /// <summary>Общая проверка видимости вложения — GetPresignedUrlAsync и GetPreviewAsync
    /// авторизуются одинаково (превью не имеет своей видимости, она наследуется от вложения,
    /// которое наследует её от родителя — та же цепочка, что описана в докстринге класса).</summary>
    private async Task<bool> HasAccessAsync(FileAttachment attachment, Guid userId, CancellationToken ct) =>
        attachment.OwnerType switch
        {
            FileOwnerType.MedicalRecord => await medicalRecords.IsVisibleToAsync(attachment.OwnerId, userId, ct),
            FileOwnerType.Medication => await HasMedicationAccessAsync(attachment.OwnerId, userId, ct),
            _ => false,
        };

    private async Task<bool> HasMedicationAccessAsync(Guid medicationId, Guid userId, CancellationToken ct)
    {
        var familyId = await db.Medications.AsNoTracking()
            .Where(m => m.Id == medicationId)
            .Select(m => m.FamilyId)
            .FirstOrDefaultAsync(ct);

        return familyId != Guid.Empty && await familyAccess.HasRoleAsync(userId, familyId, FamilyRole.Member, ct);
    }

    private static AttachmentDto ToDto(FileAttachment a) =>
        new(a.Id, a.FileName, a.ContentType, a.SizeBytes, a.UploadedAt, a.ExtractedAt, a.PreviewStatus);
}
