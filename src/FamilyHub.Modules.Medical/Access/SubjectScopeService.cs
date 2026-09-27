using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.Access;

/// <summary>Raw-данные скоупа «человек по имени субъекта»: какие семьи у меня активны, где я админ,
/// за какими взрослыми с аккаунтом я слежу — в рамках одной категории (<see cref="HealthShareCategory"/>,
/// ADR-0017). Общие для курсов приёма лекарств и прививок — оба ресурса используют одинаковую
/// трёхканальную модель доступа: свой ресурс — полностью; ресурс подопечного активной семьи — полностью
/// любому активному члену; ресурс наблюдаемого взрослого — только чтение, пока с ним есть общая активная
/// семья И он дал грант нужной категории (<c>HealthShareGrant</c>).</summary>
public record SubjectScope(
    Guid UserId,
    IReadOnlyCollection<Guid> FamilyIds,
    IReadOnlyCollection<Guid> AdminFamilyIds,
    IReadOnlyCollection<Guid> WatchedUserIds);

/// <summary>
/// Извлечено из <c>MedicationCourseAccess.GetScopeAsync</c> (ADR-0015) при добавлении прививок —
/// та же выборка нужна дословно второй раз. При введении гранта по категориям (ADR-0017) источник
/// «наблюдаемых взрослых» сменился с <c>MedicationWatcher</c> (давал доступ ко всему сразу) на
/// <c>HealthShareGrant</c> (доступ per-категория) — вызывающий указывает, какая категория ему нужна.
/// </summary>
public class SubjectScopeService(AppDbContext db, IFamilyAccessService access)
{
    /// <param name="category">Единственный бит категории, к которой нужен скоуп (Intake, Vaccinations
    /// или Diary) — комбинации не поддерживаются: у каждого ресурса своя проверка.</param>
    public async Task<SubjectScope> GetScopeAsync(Guid userId, HealthShareCategory category, CancellationToken ct = default)
    {
        var familyIds = await access.GetActiveFamilyIdsAsync(userId, ct);
        var adminFamilyIds = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.UserId == userId && m.Status == MemberStatus.Active && m.Role == FamilyRole.Admin)
            .Select(m => m.FamilyId)
            .ToListAsync(ct);

        // Наблюдатель за взрослым видит его ресурсы этой категории, только пока они состоят в общей
        // активной семье — EF транслирует побитовое И в SQL (Categories хранится как int).
        var grantedUserIds = await db.HealthShareGrants.AsNoTracking()
            .Where(g => g.ViewerUserId == userId && (g.Categories & category) == category)
            .Select(g => g.OwnerUserId)
            .ToListAsync(ct);

        List<Guid> watchedUserIds;
        if (grantedUserIds.Count > 0 && familyIds.Count > 0)
        {
            watchedUserIds = await db.FamilyMembers.AsNoTracking()
                .Where(m => m.Status == MemberStatus.Active && familyIds.Contains(m.FamilyId) && grantedUserIds.Contains(m.UserId))
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
