using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.MedicationCourses;
using FamilyHub.Infrastructure.Notifications;
using FamilyHub.Infrastructure.Persistence;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.MedicationCourses;

/// <summary>
/// Раз в час (Hangfire recurring, ADR-0015): предупреждает, что лекарство в аптечке заканчивается
/// (один раз, пока запас не пополнят), завершает курсы, у которых прошла дата окончания и не осталось
/// незакрытых приёмов, и чистит просроченные токены push-кнопок. Тексты без названия препарата —
/// по той же причине, что и у MedicationDoseScanJob.
/// </summary>
[DisableConcurrentExecution(300)]
[AutomaticRetry(Attempts = 0)]
public class MedicationCourseMaintenanceJob(
    AppDbContext db,
    NotificationSendingService notifications,
    MedicationReminderRecipients recipients,
    MedkitStockService stock,
    CourseSubjects subjects,
    ILogger<MedicationCourseMaintenanceJob> logger)
{
    /// <summary>Токен живёт до конца срока приёма плюс сутки; после ещё суток запаса его удаляем.</summary>
    private static readonly TimeSpan TokenRetention = TimeSpan.FromDays(1);

    public async Task RunAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await CheckStockAsync(now, ct);
        await CompleteFinishedCoursesAsync(now, ct);

        var purged = await db.DoseActionTokens.Where(t => t.ExpiresAt < now - TokenRetention).ExecuteDeleteAsync(ct);
        if (purged > 0) logger.LogInformation("Удалено просроченных токенов push-кнопок: {Count}", purged);
    }

    private async Task CheckStockAsync(DateTime now, CancellationToken ct)
    {
        var courses = await db.MedicationCourses
            .Where(c => c.Status == MedicationCourseStatus.Active && c.WriteOffEnabled && c.MedicationId != null)
            .ToListAsync(ct);
        if (courses.Count == 0) return;

        var people = await subjects.ResolveAsync(courses, Guid.Empty, ct);
        var infos = new Dictionary<Guid, MedkitStockService.StockInfo>();
        foreach (var id in courses.Select(c => c.MedicationId!.Value).Distinct())
        {
            var info = await stock.GetAsync(id, ct);
            if (info is not null) infos[id] = info;
        }

        foreach (var course in courses)
        {
            var days = MedicationCourseService.StockFor(course, infos, now)?.DaysCovered;
            if (days is null) continue;

            if (days > course.LowStockDays)
            {
                if (course.LowStockNotifiedAt is not null) course.LowStockNotifiedAt = null; // запас пополнили — можно предупредить снова
                continue;
            }
            if (course.LowStockNotifiedAt is not null) continue;

            var subject = CourseSubjects.Of(people, course);
            foreach (var userId in await recipients.ForStockAsync(course, ct))
            {
                var body = subject is { } s && course.SubjectUserId != userId
                    ? $"{s.Name}: проверьте аптечку"
                    : "Проверьте аптечку";
                await notifications.NotifyAsync([userId], null, NotificationType.MedicationStockLow,
                    "Лекарство заканчивается", body, course.Id,
                    _ => $"dose-stock:{course.Id}:{userId}:{now:yyyyMMdd}", ct, NotificationRelatedKind.MedicationCourse);
            }
            course.LowStockNotifiedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task CompleteFinishedCoursesAsync(DateTime now, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(now);
        var candidates = await db.MedicationCourses
            .Where(c => c.Status == MedicationCourseStatus.Active && c.EndDate != null && c.EndDate < today)
            .ToListAsync(ct);

        foreach (var course in candidates)
        {
            var localToday = DoseScheduleExpander.LocalDate(now, TimeZones.Resolve(course.TimeZoneId));
            if (course.EndDate >= localToday) continue;

            var unresolved = await db.MedicationDoses.AnyAsync(d =>
                d.CourseId == course.Id && (d.Status == DoseStatus.Pending || d.Status == DoseStatus.Snoozed), ct);
            if (unresolved) continue; // минутная задача сначала переведёт их в «пропущен»

            course.Status = MedicationCourseStatus.Completed;
            course.CompletedAt = now;
            course.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }
}
