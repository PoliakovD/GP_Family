using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Access;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.MedicationCourses;

/// <summary>Уровень доступа к курсу: Watch — только чтение («вы следите»), Full — просмотр, правка и отметки.</summary>
public enum CourseAccess { None = 0, Watch = 1, Full = 2 }

/// <summary>
/// Кто какие курсы видит (ADR-0015). Три канала: свой курс; курс подопечного семьи, где я активный
/// член (любой член может вести и отмечать — как и с самим подопечным); курс взрослого члена семьи,
/// который выбрал меня наблюдателем и с которым у нас по-прежнему есть общая активная семья
/// (выйдя из семьи, наблюдатель теряет доступ). Всё остальное для вызывающего неотличимо от
/// несуществующего.
/// </summary>
public class CourseScope(
    Guid userId, IReadOnlyCollection<Guid> familyIds, IReadOnlyCollection<Guid> adminFamilyIds,
    IReadOnlyCollection<Guid> watchedUserIds)
{
    public Guid UserId { get; } = userId;
    public IReadOnlyCollection<Guid> FamilyIds { get; } = familyIds;
    public IReadOnlyCollection<Guid> AdminFamilyIds { get; } = adminFamilyIds;

    /// <summary>Взрослые с аккаунтом, чьи курсы я вижу как наблюдатель.</summary>
    public IReadOnlyCollection<Guid> WatchedUserIds { get; } = watchedUserIds;

    /// <summary>Курсы, видимые пользователю (SQL-предикат по трём каналам).</summary>
    public IQueryable<MedicationCourse> Apply(IQueryable<MedicationCourse> query)
    {
        var uid = UserId;
        var families = FamilyIds.ToList();
        var watched = WatchedUserIds.ToList();
        return query.Where(c =>
            c.SubjectUserId == uid
            || (c.FamilyDependentId != null && c.FamilyId != null && families.Contains(c.FamilyId.Value))
            || (c.SubjectUserId != null && watched.Contains(c.SubjectUserId.Value)));
    }

    public CourseAccess AccessTo(MedicationCourse c)
    {
        if (c.SubjectUserId == UserId) return CourseAccess.Full;
        if (c.FamilyDependentId is not null && c.FamilyId is { } f && FamilyIds.Contains(f)) return CourseAccess.Full;
        if (c.SubjectUserId is { } s && WatchedUserIds.Contains(s)) return CourseAccess.Watch;
        return CourseAccess.None;
    }

    /// <summary>Удалить курс: владелец своего; для курса подопечного — создатель или админ семьи.</summary>
    public bool CanDelete(MedicationCourse c) =>
        c.SubjectUserId == UserId
        || (c.FamilyDependentId is not null && c.FamilyId is { } f
            && (c.CreatedByUserId == UserId || AdminFamilyIds.Contains(f)));
}

public class MedicationCourseAccess(AppDbContext db, SubjectScopeService scopeService)
{
    /// <summary>Raw-выборка (семьи/админство/наблюдаемые) — общая с прививками, см. SubjectScopeService.
    /// Здесь только оборачивается в CourseScope, поведение не изменилось.</summary>
    public async Task<CourseScope> GetScopeAsync(Guid userId, CancellationToken ct = default)
    {
        var s = await scopeService.GetScopeAsync(userId, ct);
        return new CourseScope(s.UserId, s.FamilyIds, s.AdminFamilyIds, s.WatchedUserIds);
    }

    /// <summary>Загрузить курс (с отслеживанием) и уровень доступа к нему; чужой — (null, None).</summary>
    public async Task<(MedicationCourse? Course, CourseAccess Access, CourseScope Scope)> LoadAsync(
        Guid userId, Guid courseId, bool tracking, CancellationToken ct = default)
    {
        var scope = await GetScopeAsync(userId, ct);
        var query = tracking ? db.MedicationCourses : db.MedicationCourses.AsNoTracking();
        var course = await scope.Apply(query).FirstOrDefaultAsync(c => c.Id == courseId, ct);
        return course is null ? (null, CourseAccess.None, scope) : (course, scope.AccessTo(course), scope);
    }
}
