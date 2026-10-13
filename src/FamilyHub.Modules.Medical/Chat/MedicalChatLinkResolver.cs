using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Audit;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Chat;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.MedicalRecords;
using FamilyHub.Modules.Medical.MedicationCourses;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.Chat;

/// <summary>
/// Карточки ссылок чата на медицинские сущности (ADR-0019 п. 5). Видимость — ровно та же, что у экранов
/// этих сущностей: мед-запись — <see cref="MedicalRecordVisibility"/>, курс — <see cref="MedicationCourseAccess"/>
/// (грант категории Intake, ADR-0017), аптечка — членство в её семье. Своей логики доступа здесь нет.
/// </summary>
public class MedicalChatLinkResolver(
    AppDbContext db,
    MedicationCourseAccess courseAccess,
    IFamilyAccessService familyAccess,
    IMedicalAuditWriter audit) : IChatLinkResolver
{
    public bool CanResolve(ChatLinkKind kind) =>
        kind is ChatLinkKind.MedicalRecord or ChatLinkKind.MedicationCourse or ChatLinkKind.Medkit;

    public async Task<ChatLinkPreview> ResolveAsync(
        ChatLinkKind kind, Guid entityId, Guid viewerUserId, CancellationToken ct = default) =>
        kind switch
        {
            ChatLinkKind.MedicalRecord => await ResolveRecordAsync(entityId, viewerUserId, ct),
            ChatLinkKind.MedicationCourse => await ResolveCourseAsync(entityId, viewerUserId, ct),
            ChatLinkKind.Medkit => await ResolveMedkitAsync(entityId, viewerUserId, ct),
            _ => ChatLinkPreview.Unavailable(kind, entityId),
        };

    private async Task<ChatLinkPreview> ResolveRecordAsync(Guid recordId, Guid viewerUserId, CancellationToken ct)
    {
        // Title зашифрован — материализуем сущность, а не проецируем в SQL.
        var record = await MedicalRecordVisibility.Visible(db, viewerUserId)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == recordId, ct);
        if (record is null) return ChatLinkPreview.Unavailable(ChatLinkKind.MedicalRecord, recordId);

        // Заголовок чужой (расшаренной) записи — тоже факт доступа к чужим медданным, как и строка в списке
        // записей (MedicalRecordService.GetVisibleRecordsAsync).
        if (record.OwnerUserId != viewerUserId)
            await audit.WriteAsync(viewerUserId, MedicalAccessAction.ViewList, ownerUserId: record.OwnerUserId, medicalRecordId: recordId, ct: ct);

        var isVisit = record.Kind == MedicalRecordKind.DoctorVisit;
        var fallbackTitle = isVisit ? "Приём врача" : "Анализ";
        return new ChatLinkPreview(
            ChatLinkKind.MedicalRecord, recordId, true,
            string.IsNullOrWhiteSpace(record.Title) ? fallbackTitle : record.Title,
            $"{fallbackTitle} · {record.RecordDate:dd.MM.yyyy}",
            isVisit ? NotificationRelatedKind.MedicalRecordVisit : NotificationRelatedKind.MedicalRecordAnalysis);
    }

    private async Task<ChatLinkPreview> ResolveCourseAsync(Guid courseId, Guid viewerUserId, CancellationToken ct)
    {
        var (course, access, _) = await courseAccess.LoadAsync(viewerUserId, courseId, tracking: false, ct);
        if (course is null || access == CourseAccess.None)
            return ChatLinkPreview.Unavailable(ChatLinkKind.MedicationCourse, courseId);

        return new ChatLinkPreview(
            ChatLinkKind.MedicationCourse, courseId, true, course.DrugName, "Курс приёма",
            NotificationRelatedKind.MedicationCourse);
    }

    private async Task<ChatLinkPreview> ResolveMedkitAsync(Guid medkitId, Guid viewerUserId, CancellationToken ct)
    {
        var medkit = await db.Medkits.AsNoTracking()
            .Where(m => m.Id == medkitId)
            .Select(m => new { m.FamilyId, m.Name, Count = m.Medications.Count })
            .FirstOrDefaultAsync(ct);
        if (medkit is null || !await familyAccess.HasRoleAsync(viewerUserId, medkit.FamilyId, FamilyRole.Member, ct))
            return ChatLinkPreview.Unavailable(ChatLinkKind.Medkit, medkitId);

        return new ChatLinkPreview(
            ChatLinkKind.Medkit, medkitId, true, medkit.Name, $"Аптечка · препаратов: {medkit.Count}",
            NotificationRelatedKind.Medkit);
    }
}
