using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.MedicationCourses;

public enum ReminderSettingsResult { Success, NotFound, Invalid }

/// <summary>
/// Настройки напоминаний человека: кто узнает о его пропусках («мои наблюдатели»), за кем следит он
/// сам (подопечные семей и взрослые, выбравшие его наблюдателем) и тихие часы. Числа напоминаний
/// (повтор, порог пропуска, запас) — на самом курсе, а не здесь.
/// </summary>
public class MedicationReminderSettingsService(AppDbContext db, IFamilyAccessService access, CourseSubjects subjects)
{
    public async Task<ReminderSettingsResponse> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.TimeZoneId, u.QuietHoursFrom, u.QuietHoursTo }).FirstAsync(ct);
        var familyIds = await access.GetActiveFamilyIdsAsync(userId, ct);

        // Мои наблюдатели: любой активный член моих семей.
        var memberIds = await ActiveMemberIdsAsync(userId, familyIds, ct);
        var myWatcherIds = (await db.MedicationWatchers.AsNoTracking()
            .Where(w => w.SubjectUserId == userId).Select(w => w.WatcherUserId).ToListAsync(ct)).ToHashSet();
        var memberNames = await subjects.UserNamesAsync(memberIds, ct);
        var candidates = memberIds
            .Select(id => new WatcherCandidateDto(id, memberNames.GetValueOrDefault(id, CourseSubjects.UnknownName), myWatcherIds.Contains(id)))
            .OrderBy(c => c.Name).ToList();

        var watching = new List<WatchingDto>();

        // Подопечные моих семей: включить «я слежу» можно за любым.
        var dependents = await db.FamilyDependents.AsNoTracking().Where(d => familyIds.Contains(d.FamilyId)).ToListAsync(ct);
        if (dependents.Count > 0)
        {
            var depIds = dependents.Select(d => d.Id).ToList();
            var mine = await db.MedicationWatchers.AsNoTracking()
                .Where(w => w.WatcherUserId == userId && w.FamilyDependentId != null && depIds.Contains(w.FamilyDependentId.Value))
                .ToListAsync(ct);
            var counts = await CourseCountsAsync(c => c.FamilyDependentId != null && depIds.Contains(c.FamilyDependentId.Value),
                c => c.FamilyDependentId!.Value, ct);
            foreach (var d in dependents.OrderBy(d => d.FirstName))
            {
                var row = mine.FirstOrDefault(w => w.FamilyDependentId == d.Id);
                watching.Add(new WatchingDto(CourseSubjects.DependentKind, d.Id, d.FirstName, counts.GetValueOrDefault(d.Id),
                    IsWatching: row is not null, NotifyMissed: row?.NotifyMissed ?? false, ReceiveReminders: row?.ReceiveReminders ?? false));
            }
        }

        // Взрослые, выбравшие меня наблюдателем (пока есть общая семья): можно только приглушить пропуски.
        var adultRows = await db.MedicationWatchers.AsNoTracking()
            .Where(w => w.WatcherUserId == userId && w.SubjectUserId != null).ToListAsync(ct);
        var adultIds = adultRows.Select(w => w.SubjectUserId!.Value).Where(memberIds.Contains).ToList();
        if (adultIds.Count > 0)
        {
            var counts = await CourseCountsAsync(c => c.SubjectUserId != null && adultIds.Contains(c.SubjectUserId.Value),
                c => c.SubjectUserId!.Value, ct);
            foreach (var w in adultRows.Where(w => adultIds.Contains(w.SubjectUserId!.Value)))
            {
                var id = w.SubjectUserId!.Value;
                watching.Add(new WatchingDto(CourseSubjects.UserKind, id, memberNames.GetValueOrDefault(id, CourseSubjects.UnknownName),
                    counts.GetValueOrDefault(id), IsWatching: true, w.NotifyMissed, ReceiveReminders: false));
            }
        }

        return new ReminderSettingsResponse(user.TimeZoneId, user.QuietHoursFrom, user.QuietHoursTo, candidates, watching);
    }

    /// <summary>Заменяет набор моих наблюдателей: остаются только активные члены моих семей.</summary>
    public async Task<ReminderSettingsResult> SetMyWatchersAsync(Guid userId, SetMyWatchersRequest request, CancellationToken ct = default)
    {
        var wanted = (request.UserIds ?? []).Where(id => id != userId).Distinct().ToList();
        var familyIds = await access.GetActiveFamilyIdsAsync(userId, ct);
        var allowed = (await ActiveMemberIdsAsync(userId, familyIds, ct)).ToHashSet();
        if (wanted.Any(id => !allowed.Contains(id))) return ReminderSettingsResult.Invalid;

        var existing = await db.MedicationWatchers.Where(w => w.SubjectUserId == userId).ToListAsync(ct);
        db.MedicationWatchers.RemoveRange(existing.Where(w => !wanted.Contains(w.WatcherUserId)));

        var now = DateTime.UtcNow;
        foreach (var id in wanted.Where(id => existing.All(w => w.WatcherUserId != id)))
        {
            db.MedicationWatchers.Add(new MedicationWatcher
            {
                Id = Guid.NewGuid(), SubjectUserId = userId, WatcherUserId = id,
                ReceiveReminders = false, NotifyMissed = true, CreatedAt = now,
            });
        }
        await db.SaveChangesAsync(ct);
        return ReminderSettingsResult.Success;
    }

    /// <summary>Настройка моего наблюдения. Подопечный: включить/выключить «я слежу» и что получать
    /// (оба флага выключены — строка удаляется). Взрослый: только приглушить уведомления о пропусках —
    /// выбирать себя наблюдателем может лишь сам взрослый.</summary>
    public async Task<ReminderSettingsResult> SetWatchingAsync(
        Guid userId, string kind, Guid id, SetWatchingRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        if (kind == CourseSubjects.DependentKind)
        {
            var familyId = await db.FamilyDependents.AsNoTracking().Where(d => d.Id == id)
                .Select(d => (Guid?)d.FamilyId).FirstOrDefaultAsync(ct);
            var familyIds = await access.GetActiveFamilyIdsAsync(userId, ct);
            if (familyId is null || !familyIds.Contains(familyId.Value)) return ReminderSettingsResult.NotFound;

            var row = await db.MedicationWatchers.FirstOrDefaultAsync(w => w.FamilyDependentId == id && w.WatcherUserId == userId, ct);
            var off = !request.NotifyMissed && !request.ReceiveReminders;
            if (row is null && !off)
            {
                db.MedicationWatchers.Add(new MedicationWatcher
                {
                    Id = Guid.NewGuid(), FamilyDependentId = id, WatcherUserId = userId,
                    ReceiveReminders = request.ReceiveReminders, NotifyMissed = request.NotifyMissed, CreatedAt = now,
                });
            }
            else if (row is not null && off) db.MedicationWatchers.Remove(row);
            else if (row is not null)
            {
                row.ReceiveReminders = request.ReceiveReminders;
                row.NotifyMissed = request.NotifyMissed;
            }
        }
        else if (kind == CourseSubjects.UserKind)
        {
            var row = await db.MedicationWatchers.FirstOrDefaultAsync(w => w.SubjectUserId == id && w.WatcherUserId == userId, ct);
            if (row is null) return ReminderSettingsResult.NotFound;
            row.NotifyMissed = request.NotifyMissed;
        }
        else
        {
            return ReminderSettingsResult.Invalid;
        }

        await db.SaveChangesAsync(ct);
        return ReminderSettingsResult.Success;
    }

    /// <summary>Тихие часы: оба значения либо ни одного (выключено); равные границы не имеют смысла.</summary>
    public async Task<ReminderSettingsResult> SetQuietHoursAsync(Guid userId, QuietHoursRequest request, CancellationToken ct = default)
    {
        if ((request.From is null) != (request.To is null)) return ReminderSettingsResult.Invalid;
        if (request.From is { } f && request.To is { } t && f == t) return ReminderSettingsResult.Invalid;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return ReminderSettingsResult.NotFound;
        user.QuietHoursFrom = request.From;
        user.QuietHoursTo = request.To;
        await db.SaveChangesAsync(ct);
        return ReminderSettingsResult.Success;
    }

    private async Task<List<Guid>> ActiveMemberIdsAsync(Guid userId, IReadOnlyCollection<Guid> familyIds, CancellationToken ct) =>
        familyIds.Count == 0
            ? []
            : await db.FamilyMembers.AsNoTracking()
                .Where(m => familyIds.Contains(m.FamilyId) && m.Status == MemberStatus.Active && m.UserId != userId)
                .Select(m => m.UserId).Distinct().ToListAsync(ct);

    private async Task<Dictionary<Guid, int>> CourseCountsAsync(
        System.Linq.Expressions.Expression<Func<MedicationCourse, bool>> filter,
        System.Linq.Expressions.Expression<Func<MedicationCourse, Guid>> key, CancellationToken ct) =>
        await db.MedicationCourses.AsNoTracking()
            .Where(c => c.Status != MedicationCourseStatus.Completed)
            .Where(filter)
            .GroupBy(key)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
}
