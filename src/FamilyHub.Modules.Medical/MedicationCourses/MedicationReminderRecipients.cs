using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.MedicationCourses;

/// <summary>
/// Кому адресовать уведомления о курсе (ADR-0015). Напоминание о приёме получает тот, кто принимает
/// (свой курс) или дают лекарство (наблюдатели подопечного с ReceiveReminders). О пропуске узнают
/// наблюдатели — но только пока они состоят в общей активной семье. «Пропустить» — осознанный отказ:
/// о нём не сообщаем вовсе (это решает вызывающий, здесь получатели считаются только по факту пропуска).
///
/// ADR-0017: для взрослого субъекта наблюдатель без гранта <c>HealthShareGrant.Intake</c> уведомлений
/// не получает — доступ мог быть отозван из настроек «Кто видит моё здоровье» уже после того, как
/// наблюдатель был выбран в MedicationWatcher (снятие watcher-строки грант не трогает, но не наоборот:
/// см. class doc MedicationReminderSettingsService).
/// </summary>
public class MedicationReminderRecipients(AppDbContext db)
{
    /// <summary>Кому напомнить, что пора принять.</summary>
    public async Task<List<Guid>> ForDueAsync(MedicationCourse course, CancellationToken ct = default)
    {
        if (course.SubjectUserId is { } owner) return [owner];

        var watchers = await WatchersOfDependentAsync(course, w => w.ReceiveReminders, ct);
        return watchers;
    }

    /// <summary>Кого известить, что приём не отмечен. Сам человек (свой курс) не получает — он видит
    /// пропуск на экране «Сегодня».</summary>
    public async Task<List<Guid>> ForMissedAsync(MedicationCourse course, CancellationToken ct = default)
    {
        if (course.SubjectUserId is { } subject)
        {
            var watcherIds = await db.MedicationWatchers.AsNoTracking()
                .Where(w => w.SubjectUserId == subject && w.NotifyMissed)
                .Select(w => w.WatcherUserId).ToListAsync(ct);
            if (watcherIds.Count == 0) return [];

            watcherIds = await db.HealthShareGrants.AsNoTracking()
                .Where(g => g.OwnerUserId == subject && watcherIds.Contains(g.ViewerUserId)
                    && (g.Categories & HealthShareCategory.Intake) == HealthShareCategory.Intake)
                .Select(g => g.ViewerUserId)
                .ToListAsync(ct);
            return await SharingFamilyAsync(subject, watcherIds, ct);
        }

        return await WatchersOfDependentAsync(course, w => w.ReceiveReminders || w.NotifyMissed, ct);
    }

    /// <summary>Кого предупредить, что запас заканчивается: владельца или тех, кто дают лекарство.</summary>
    public Task<List<Guid>> ForStockAsync(MedicationCourse course, CancellationToken ct = default) =>
        ForDueAsync(course, ct);

    private async Task<List<Guid>> WatchersOfDependentAsync(
        MedicationCourse course, Func<MedicationWatcher, bool> predicate, CancellationToken ct)
    {
        if (course.FamilyDependentId is not { } dependentId || course.FamilyId is not { } familyId) return [];

        var rows = await db.MedicationWatchers.AsNoTracking().Where(w => w.FamilyDependentId == dependentId).ToListAsync(ct);
        var ids = rows.Where(predicate).Select(w => w.WatcherUserId).ToList();
        if (ids.Count == 0) return [];

        // Вышедший из семьи наблюдатель уведомлений больше не получает.
        return await db.FamilyMembers.AsNoTracking()
            .Where(m => m.FamilyId == familyId && m.Status == MemberStatus.Active && ids.Contains(m.UserId))
            .Select(m => m.UserId).Distinct().ToListAsync(ct);
    }

    private async Task<List<Guid>> SharingFamilyAsync(Guid subjectUserId, List<Guid> watcherIds, CancellationToken ct)
    {
        if (watcherIds.Count == 0) return [];

        var subjectFamilies = db.FamilyMembers.Where(m => m.UserId == subjectUserId && m.Status == MemberStatus.Active)
            .Select(m => m.FamilyId);
        return await db.FamilyMembers.AsNoTracking()
            .Where(m => m.Status == MemberStatus.Active && watcherIds.Contains(m.UserId) && subjectFamilies.Contains(m.FamilyId))
            .Select(m => m.UserId).Distinct().ToListAsync(ct);
    }
}
