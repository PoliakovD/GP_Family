using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Attachments;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Vaccinations;

/// <summary>
/// «Фото сертификата»: в отличие от разового распознавания препарата
/// (<see cref="VaccinationCertificateOcrService"/>/<c>MedicationOcrService</c>), здесь пользователь
/// подтверждает находки, и фото СОХРАНЯЕТСЯ («файл виден у каждой найденной прививке без
/// копирования блоба») — поэтому загрузка происходит один раз, при подтверждении, а не при
/// распознавании. Один <see cref="VaccinationCertificate"/> + один комплект файлов на всё
/// подтверждение; каждая созданная <see cref="Vaccination"/> получает один и тот же CertificateId.
/// </summary>
public class VaccinationCertificateService(
    AppDbContext db, VaccinationAccess access, VaccinationSubjects subjects, VaccinationService vaccinations,
    AttachmentService attachments, ILogger<VaccinationCertificateService> logger)
{
    public async Task<(VaccinationResult Result, BulkMarkResultDto? Item, string? Error)> ConfirmAsync(
        Guid userId, string subjectKind, Guid subjectId, IFormFileCollection files, List<BulkMarkItem> items,
        CancellationToken ct = default)
    {
        var scope = await access.GetScopeAsync(userId, ct);
        var subject = await subjects.FindAsync(userId, subjectKind, subjectId, scope, ct);
        if (subject is null) return (VaccinationResult.NotFound, null, null);
        if (!subject.CanEdit) return (VaccinationResult.Forbidden, null, null);
        if (files.Count == 0) return (VaccinationResult.Invalid, null, "Прикрепите фото сертификата.");
        if (items.Count == 0) return (VaccinationResult.Invalid, null, "Отметьте хотя бы одну прививку.");
        // Проверяем все файлы до создания сертификата — иначе полупустой сертификат остался бы в БД
        // (размер/allow-list/сигнатура, как у вложений мед-записей; аудит security-audit-2026-10).
        foreach (var file in files)
        {
            await using var probe = file.OpenReadStream();
            if (attachments.ValidateUpload(file.ContentType, file.Length, probe) != AttachmentAccessResult.Success)
                return (VaccinationResult.Invalid, null, $"Файл «{file.FileName}» не подходит: нужен снимок или PDF не больше {attachments.MaxSizeBytes / (1024 * 1024)} МБ.");
        }

        var certificate = new VaccinationCertificate
        {
            Id = Guid.NewGuid(),
            SubjectUserId = subject.Kind == VaccinationSubjects.UserKind ? subject.Id : null,
            FamilyDependentId = subject.Kind == VaccinationSubjects.DependentKind ? subject.Id : null,
            FamilyId = subject.FamilyId,
            UploadedByUserId = userId,
            CreatedAt = DateTime.UtcNow,
        };
        db.VaccinationCertificates.Add(certificate);
        await db.SaveChangesAsync(ct);

        foreach (var file in files)
        {
            await using var stream = file.OpenReadStream();
            await attachments.UploadRawAsync(
                FileOwnerType.VaccinationCertificate, certificate.Id, file.FileName, file.ContentType, file.Length, stream, ct);
        }

        var (result, saved, error) = await vaccinations.BulkMarkAsync(
            userId, new BulkMarkRequest(subjectKind, subjectId, items, certificate.Id), ct);

        logger.LogInformation(
            "Сертификат {CertificateId} загружен для {Kind}:{SubjectId} пользователем {UserId} ({Files} файлов, {Items} прививок)",
            certificate.Id, subjectKind, subjectId, userId, files.Count, items.Count);
        return (result, saved, error);
    }

    public async Task<(VaccinationResult Result, List<AttachmentDto> Items)> GetAttachmentsAsync(
        Guid userId, Guid certificateId, CancellationToken ct = default)
    {
        var (cert, level) = await access.LoadCertificateAsync(userId, certificateId, ct);
        if (cert is null || level == VaccinationAccessLevel.None) return (VaccinationResult.NotFound, []);
        return (VaccinationResult.Success, await attachments.ListRawAsync(FileOwnerType.VaccinationCertificate, certificateId, ct));
    }
}
