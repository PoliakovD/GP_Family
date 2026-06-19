using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Storage;
using FamilyHub.Modules.Medical.MedicalRecords;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.Attachments;

/// <summary>
/// Метаданные сканов в БД, файлы — в объектном хранилище (раздел 5/9 брифа).
/// Вложение не имеет своей видимости — доступ наследуется от родительской записи
/// (MedicalRecord или Medication).
/// </summary>
public class AttachmentService(
    AppDbContext db,
    IFileStorage storage,
    MedicalRecordService medicalRecords,
    IFamilyAccessService familyAccess)
{
    /// <summary>Прикладывать сканы к анализу может только владелец записи — тот же барьер, что и для шаринга.</summary>
    public async Task<(AttachmentAccessResult Result, AttachmentDto? Item)> UploadForMedicalRecordAsync(
        Guid recordId, Guid ownerUserId, string fileName, string contentType, long sizeBytes, Stream content, CancellationToken ct = default)
    {
        var record = await db.MedicalRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recordId, ct);
        if (record is null) return (AttachmentAccessResult.NotFound, null);
        if (record.OwnerUserId != ownerUserId) return (AttachmentAccessResult.Forbidden, null);

        var attachmentId = Guid.NewGuid();
        var storageKey = $"medical-records/{recordId}/{attachmentId}-{fileName}";
        await storage.SaveAsync(storageKey, content, sizeBytes, contentType, ct);

        var attachment = new FileAttachment
        {
            Id = attachmentId,
            OwnerType = FileOwnerType.MedicalRecord,
            OwnerId = recordId,
            StorageKey = storageKey,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            IsEncrypted = false,
            UploadedAt = DateTime.UtcNow,
        };
        db.FileAttachments.Add(attachment);
        await db.SaveChangesAsync(ct);

        return (AttachmentAccessResult.Success, ToDto(attachment));
    }

    /// <summary>Короткоживущая ссылка на скачивание, после проверки доступа к родительской записи.</summary>
    public async Task<(AttachmentAccessResult Result, string? Url)> GetPresignedUrlAsync(
        Guid attachmentId, Guid userId, CancellationToken ct = default)
    {
        var attachment = await db.FileAttachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == attachmentId, ct);
        if (attachment is null) return (AttachmentAccessResult.NotFound, null);

        var hasAccess = attachment.OwnerType switch
        {
            FileOwnerType.MedicalRecord => await medicalRecords.IsVisibleToAsync(attachment.OwnerId, userId, ct),
            FileOwnerType.Medication => await HasMedicationAccessAsync(attachment.OwnerId, userId, ct),
            _ => false,
        };
        if (!hasAccess) return (AttachmentAccessResult.Forbidden, null);

        var url = await storage.GetPresignedUrlAsync(attachment.StorageKey, TimeSpan.FromMinutes(5), ct);
        return (AttachmentAccessResult.Success, url);
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
