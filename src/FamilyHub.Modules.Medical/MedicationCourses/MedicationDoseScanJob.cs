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
/// Раз в минуту (Hangfire recurring, ADR-0015) просматривает активные курсы и создаёт напоминания:
/// наступил приём — «пора принять», повтор через N минут, после «отложить» — снова, не отмечено за
/// порог — приём становится пропущенным и наблюдатели получают уведомление. «Пропустить» никого не
/// уведомляет. Строка приёма создаётся здесь в момент наступления, так что история и статистика —
/// просто строки. Идемпотентна: каждое уведомление защищено UNIQUE DedupKey, а строка приёма —
/// уникальным индексом (курс, время); повторный прогон, гонка с пользователем и простой джобы не
/// создают дублей — после простоя давно прошедшие приёмы сразу становятся пропущенными, а не
/// «напоминаются» задним числом.
///
/// Тексты уведомлений намеренно без названия препарата: таблица уведомлений не шифруется, а
/// Telegram пересылает текст дословно (ADR-0015); в тексте только время и, для чужого курса, имя.
/// Retry не нужен — следующая минута сделает то же самое.
/// </summary>
[DisableConcurrentExecution(55)]
[AutomaticRetry(Attempts = 0)]
public class MedicationDoseScanJob(
    AppDbContext db,
    NotificationSendingService notifications,
    MedicationReminderRecipients recipients,
    CourseSubjects subjects,
    ILogger<MedicationDoseScanJob> logger)
{
    /// <summary>Курс сканируется на приёмы не старше этого окна — после простоя джобы дальше не «догоняем».</summary>
    private static readonly TimeSpan LookBack = TimeSpan.FromDays(2);

    public async Task RunAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        // Границы по датам с запасом в сутки на часовые пояса; точное окно курса даёт expander.
        var courses = await db.MedicationCourses.AsNoTracking()
            .Where(c => c.Status == MedicationCourseStatus.Active
                        && c.StartDate <= today.AddDays(1)
                        && (c.EndDate == null || c.EndDate >= today.AddDays(-2)))
            .ToListAsync(ct);
        if (courses.Count == 0) return;

        var ids = courses.Select(c => c.Id).ToList();
        var since = now - LookBack;
        var rows = (await db.MedicationDoses
            .Where(d => ids.Contains(d.CourseId) && d.ScheduledAt >= since)
            .ToListAsync(ct)).ToLookup(d => d.CourseId);

        var people = await subjects.ResolveAsync(courses, Guid.Empty, ct);
        var zones = new Dictionary<Guid, TimeZoneInfo>();

        foreach (var course in courses)
        {
            var schedule = MedicationCourseRules.ParseSchedule(course.ScheduleJson);
            if (schedule is null || schedule.Mode == DoseScheduleMode.AsNeeded) continue;

            try
            {
                await ScanCourseAsync(course, schedule, rows[course.Id].ToDictionary(d => d.ScheduledAt!.Value), now, people, zones, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Один сломанный курс не должен глушить напоминания остальным.
                logger.LogError(ex, "Сбой сканирования курса {CourseId}", course.Id);
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task ScanCourseAsync(
        MedicationCourse course, DoseSchedule schedule, Dictionary<DateTime, MedicationDose> byTime, DateTime now,
        IReadOnlyDictionary<Guid, SubjectDto> people, Dictionary<Guid, TimeZoneInfo> zones, CancellationToken ct)
    {
        var tz = TimeZones.Resolve(course.TimeZoneId);
        var windowFrom = course.EffectiveFromUtc > now - LookBack ? course.EffectiveFromUtc : now - LookBack;
        var subject = CourseSubjects.Of(people, course);

        var occurrences = DoseScheduleExpander.Expand(schedule, course.StartDate, course.EndDate, tz, windowFrom, now.AddSeconds(1));
        foreach (var o in occurrences)
        {
            byTime.TryGetValue(o.ScheduledAtUtc, out var dose);
            var deadline = DoseTiming.MissedDeadline(o.ScheduledAtUtc, course.MissedAfterMinutes, dose?.SnoozedUntil);

            if (dose is null)
            {
                dose = new MedicationDose
                {
                    Id = Guid.NewGuid(), CourseId = course.Id, ScheduledAt = o.ScheduledAtUtc, Units = o.Units,
                    Status = now < deadline ? DoseStatus.Pending : DoseStatus.Missed,
                    RemindedAt = now < deadline ? now : null, CreatedAt = now,
                };
                db.MedicationDoses.Add(dose);
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException)
                {
                    // Человек успел отметить этот приём раньше — строка уже есть.
                    db.Entry(dose).State = EntityState.Detached;
                    continue;
                }

                if (dose.Status == DoseStatus.Pending) await SendDueAsync(course, dose, "0", subject, zones, ct);
                else await SendMissedAsync(course, dose, subject, zones, ct);
                continue;
            }

            switch (dose.Status)
            {
                case DoseStatus.Pending when now >= deadline:
                case DoseStatus.Snoozed when now >= deadline:
                    dose.Status = DoseStatus.Missed;
                    await db.SaveChangesAsync(ct);
                    await MarkDueNotificationsReadAsync(dose.Id, now, ct);
                    await SendMissedAsync(course, dose, subject, zones, ct);
                    break;

                case DoseStatus.Pending
                    when course.RepeatAfterMinutes is { } repeat && dose.RepeatSentAt is null
                         && now >= o.ScheduledAtUtc.AddMinutes(repeat):
                    dose.RepeatSentAt = now;
                    await db.SaveChangesAsync(ct);
                    await SendDueAsync(course, dose, "r", subject, zones, ct);
                    break;

                case DoseStatus.Snoozed
                    when dose.SnoozedUntil is { } until && until <= now && (dose.RemindedAt is null || dose.RemindedAt < until):
                    dose.RemindedAt = now;
                    await db.SaveChangesAsync(ct);
                    await SendDueAsync(course, dose, $"s{dose.SnoozeCount}", subject, zones, ct);
                    break;
            }
        }
    }

    private async Task SendDueAsync(
        MedicationCourse course, MedicationDose dose, string keySuffix, SubjectDto? subject,
        Dictionary<Guid, TimeZoneInfo> zones, CancellationToken ct)
    {
        foreach (var userId in await recipients.ForDueAsync(course, ct))
        {
            var time = await LocalTimeAsync(userId, dose.ScheduledAt!.Value, zones, ct);
            var forSomeoneElse = subject is { IsSelf: false } && course.SubjectUserId != userId;
            var body = forSomeoneElse
                ? $"{subject!.Name}: напоминание о приёме в {time}"
                : $"Напоминание о приёме в {time}";

            await notifications.NotifyAsync([userId], null, NotificationType.MedicationDoseDue,
                "Время принять лекарство", body, dose.Id, _ => $"dose-due:{dose.Id}:{userId}:{keySuffix}", ct,
                NotificationRelatedKind.MedicationDose);
        }
    }

    private async Task SendMissedAsync(
        MedicationCourse course, MedicationDose dose, SubjectDto? subject,
        Dictionary<Guid, TimeZoneInfo> zones, CancellationToken ct)
    {
        foreach (var userId in await recipients.ForMissedAsync(course, ct))
        {
            var time = await LocalTimeAsync(userId, dose.ScheduledAt!.Value, zones, ct);
            var who = subject?.Name ?? CourseSubjects.UnknownName;

            await notifications.NotifyAsync([userId], null, NotificationType.MedicationDoseMissed,
                "Пропущен приём лекарства", $"{who}: приём в {time} не отмечен", dose.Id,
                _ => $"dose-miss:{dose.Id}:{userId}", ct, NotificationRelatedKind.MedicationDose);
        }
    }

    private async Task<string> LocalTimeAsync(Guid userId, DateTime utc, Dictionary<Guid, TimeZoneInfo> zones, CancellationToken ct)
    {
        if (!zones.TryGetValue(userId, out var tz))
        {
            var id = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.TimeZoneId).FirstOrDefaultAsync(ct);
            zones[userId] = tz = TimeZones.Resolve(id);
        }
        return TimeZoneInfo.ConvertTimeFromUtc(utc, tz).ToString("H:mm");
    }

    /// <summary>Устаревшие напоминания о приёме, ставшем пропущенным, из ленты убираем.</summary>
    private Task<int> MarkDueNotificationsReadAsync(Guid doseId, DateTime now, CancellationToken ct) =>
        db.Notifications
            .Where(n => n.RelatedEntityId == doseId && n.Type == NotificationType.MedicationDoseDue && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true).SetProperty(n => n.ReadAt, now), ct);
}
