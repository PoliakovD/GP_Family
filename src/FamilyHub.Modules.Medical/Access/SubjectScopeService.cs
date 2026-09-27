using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.Access;

/// <summary>Raw-данные скоупа «человек по имени субъекта»: какие семьи у меня активны, где я админ,
/// за какими взрослыми с аккаунтом я слежу. Общие для курсов приёма лекарств и прививок — оба ресурса
/// используют одну и ту же таблицу наблюдателей (<c>MedicationWatcher</c>, имя историческое — завели
/// для курсов первыми) и одинаковую трёхканальную модель доступа: свой ресурс — полностью; ресурс
/// подопечного активной семьи — полностью любому активному члену; ресурс наблюдаемого взрослого —
/// только чтение, пока с ним есть общая активная семья.</summary>
public record SubjectScope(
    Guid UserId,
    IReadOnlyCollection<Guid> FamilyIds,
    IReadOnlyCollection<Guid> AdminFamilyIds,
    IReadOnlyCollection<Guid> WatchedUserIds);

/// <summary>
/// Извлечено из <c>MedicationCourseAccess.GetScopeAsync</c> (ADR-0015) при добавлении прививок —
/// та же выборка нужна дословно второй раз. Поведение курсов не меняется: <c>MedicationCourseAccess</c>
/// теперь только оборачивает эти данные в свой <c>CourseScope</c>, вся логика — здесь.
/// </summary>
public class SubjectScopeService(AppDbContext db, IFamilyAccessService access)
{
    public async Task<SubjectScope> GetScopeAsync(Guid userId, CancellationToken ct = default)
    {
        var familyIds = await access.GetActiveFamilyIdsAsync(userId, ct);
        var adminFamilyIds = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.UserId == userId && m.Status == MemberStatus.Active && m.Role == FamilyRole.Admin)
            .Select(m => m.FamilyId)
            .ToListAsync(ct);

        // Наблюдатель за взрослым видит его ресурсы, только пока они состоят в общей активной семье.
        var watchedUserIds = await db.MedicationWatchers.AsNoTracking()
            .Where(w => w.WatcherUserId == userId && w.SubjectUserId != null)
            .Select(w => w.SubjectUserId!.Value)
            .ToListAsync(ct);
        if (watchedUserIds.Count > 0 && familyIds.Count > 0)
        {
            watchedUserIds = await db.FamilyMembers.AsNoTracking()
                .Where(m => m.Status == MemberStatus.Active && familyIds.Contains(m.FamilyId) && watchedUserIds.Contains(m.UserId))
                .Select(m => m.UserId)
                .Distinct()
                .ToListAsync(ct);
        }
        else
        {
            watchedUserIds = [];
        }

        return new SubjectScope(userId, familyIds, adminFamilyIds, watchedUserIds);
    }
}
