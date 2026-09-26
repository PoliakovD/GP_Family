using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.MedicalRecords;

/// <summary>
/// Единое правило видимости мед-записей (раздел 6 брифа). Видно, если: владелец, ИЛИ запись назначена
/// лично этому пользователю (TargetUserId), ИЛИ (мои анализы расшарены этой семье И я в ней состою
/// активным членом И запись не скрыта именно от неё), ИЛИ запись привязана к подопечному семьи, где я
/// активный член. Вынесено из MedicalRecordService, чтобы им пользовались и другие сервисы модуля
/// (назначения для курсов приёма), не копируя предикат.
/// </summary>
internal static class MedicalRecordVisibility
{
    public static IQueryable<MedicalRecord> Visible(AppDbContext db, Guid userId, MedicalRecordKind? kind = null)
    {
        var query = db.MedicalRecords.AsNoTracking().Where(r =>
            r.OwnerUserId == userId
            || r.TargetUserId == userId
            || db.FamilyMedicalShares.Any(share =>
                   share.OwnerUserId == r.OwnerUserId &&
                   db.FamilyMembers.Any(m =>
                       m.FamilyId == share.FamilyId &&
                       m.UserId == userId &&
                       m.Status == MemberStatus.Active) &&
                   !db.MedicalRecordHiddens.Any(h =>
                       h.MedicalRecordId == r.Id &&
                       h.FamilyId == share.FamilyId))
            || (r.FamilyDependentId != null && db.FamilyMembers.Any(m =>
                   m.UserId == userId &&
                   m.Status == MemberStatus.Active &&
                   db.FamilyDependents.Any(d => d.Id == r.FamilyDependentId && d.FamilyId == m.FamilyId))));

        return kind is null ? query : query.Where(r => r.Kind == kind);
    }
}
