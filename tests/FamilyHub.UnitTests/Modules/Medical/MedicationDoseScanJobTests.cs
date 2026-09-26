using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.MedicationCourses;
using FamilyHub.Infrastructure.Notifications;
using FamilyHub.Modules.Medical.MedicationCourses;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical;

public class MedicationDoseScanJobTests : MedicationCourseTestBase
{
    private readonly MedicationDoseScanJob _job;
    private readonly MedicationCourseMaintenanceJob _maintenance;

    public MedicationDoseScanJobTests()
    {
        var notifications = new NotificationSendingService(Db, [], NullLogger<NotificationSendingService>.Instance);
        var recipients = new MedicationReminderRecipients(Db);
        var subjects = new CourseSubjects(Db);
        _job = new MedicationDoseScanJob(Db, notifications, recipients, subjects, NullLogger<MedicationDoseScanJob>.Instance);
        _maintenance = new MedicationCourseMaintenanceJob(Db, notifications, recipients,
            new MedkitStockService(Db, new FamilyHub.Infrastructure.Authorization.FamilyAccessService(
                Db, NullLogger<FamilyHub.Infrastructure.Authorization.FamilyAccessService>.Instance)),
            subjects, NullLogger<MedicationCourseMaintenanceJob>.Instance);
    }

    private List<Notification> Sent(NotificationType? type = null) =>
        Db.Notifications.AsNoTracking().Where(n => type == null || n.Type == type).OrderBy(n => n.CreatedAt).ToList();

    private MedicationCourse SeedDue(Guid userId, Guid familyId, DateTime at, int? repeat = 15, string name = "Секретин")
    {
        var course = SeedCourse(userId, null, familyId, userId, OnceAt(at), at.AddMinutes(-1), name: name);
        course.RepeatAfterMinutes = repeat;
        Db.SaveChanges();
        return course;
    }

    private void Watch(Guid subjectUserId, Guid watcherId, bool notifyMissed = true)
    {
        Db.MedicationWatchers.Add(new MedicationWatcher
        {
            Id = Guid.NewGuid(), SubjectUserId = subjectUserId, WatcherUserId = watcherId, NotifyMissed = notifyMissed, CreatedAt = DateTime.UtcNow,
        });
        Db.SaveChanges();
    }

    // ── Напоминание о приёме ──────────────────────────────────────────────

    [Fact]
    public async Task DueDose_CreatesPendingRow_AndNotifiesOwner_WithoutDrugName()
    {
        var (family, admin) = SeedFamily();
        var course = SeedDue(admin.Id, family.Id, MinuteFromNow(-1));

        await _job.RunAsync();

        var dose = Db.MedicationDoses.AsNoTracking().Should().ContainSingle().Subject;
        dose.Status.Should().Be(DoseStatus.Pending);
        dose.RemindedAt.Should().NotBeNull();
        var n = Sent(NotificationType.MedicationDoseDue).Should().ContainSingle().Subject;
        n.UserId.Should().Be(admin.Id);
        n.RelatedEntityId.Should().Be(dose.Id);
        n.RelatedEntityKind.Should().Be(NotificationRelatedKind.MedicationDose);
        (n.Title + n.Body).Should().NotContain(course.DrugName);
        n.Body.Should().MatchRegex(@"\d{1,2}:\d{2}");
    }

    [Fact]
    public async Task RunTwice_DoesNotDuplicate()
    {
        var (family, admin) = SeedFamily();
        SeedDue(admin.Id, family.Id, MinuteFromNow(-1));

        await _job.RunAsync();
        await _job.RunAsync();

        Db.MedicationDoses.Should().ContainSingle();
        Sent().Should().ContainSingle();
    }

    [Fact]
    public async Task Repeat_SentOnceAfterConfiguredMinutes()
    {
        var (family, admin) = SeedFamily();
        SeedDue(admin.Id, family.Id, MinuteFromNow(-20), repeat: 15);

        await _job.RunAsync(); // создаёт приём и первое напоминание
        Sent(NotificationType.MedicationDoseDue).Should().HaveCount(1);

        await _job.RunAsync(); // прошло 15 минут — повтор
        Sent(NotificationType.MedicationDoseDue).Should().HaveCount(2);

        await _job.RunAsync();
        Sent(NotificationType.MedicationDoseDue).Should().HaveCount(2);
    }

    [Fact]
    public async Task NoRepeat_WhenNotConfigured()
    {
        var (family, admin) = SeedFamily();
        SeedDue(admin.Id, family.Id, MinuteFromNow(-20), repeat: null);

        await _job.RunAsync();
        await _job.RunAsync();

        Sent(NotificationType.MedicationDoseDue).Should().HaveCount(1);
    }

    [Fact]
    public async Task SnoozeExpired_RemindsAgain_OncePerSnooze()
    {
        var (family, admin) = SeedFamily();
        var at = MinuteFromNow(-20);
        var course = SeedDue(admin.Id, family.Id, at, repeat: null);
        Db.MedicationDoses.Add(new MedicationDose
        {
            Id = Guid.NewGuid(), CourseId = course.Id, ScheduledAt = at, Units = 1, Status = DoseStatus.Snoozed,
            SnoozedUntil = DateTime.UtcNow.AddMinutes(-1), SnoozeCount = 1, RemindedAt = at, CreatedAt = at,
        });
        Db.SaveChanges();

        await _job.RunAsync();
        await _job.RunAsync();

        Sent(NotificationType.MedicationDoseDue).Should().ContainSingle();
    }

    [Fact]
    public async Task SnoozeStillRunning_DoesNotRemind()
    {
        var (family, admin) = SeedFamily();
        var at = MinuteFromNow(-20);
        var course = SeedDue(admin.Id, family.Id, at, repeat: null);
        Db.MedicationDoses.Add(new MedicationDose
        {
            Id = Guid.NewGuid(), CourseId = course.Id, ScheduledAt = at, Units = 1, Status = DoseStatus.Snoozed,
            SnoozedUntil = DateTime.UtcNow.AddMinutes(10), SnoozeCount = 1, RemindedAt = at, CreatedAt = at,
        });
        Db.SaveChanges();

        await _job.RunAsync();

        Sent().Should().BeEmpty();
    }

    [Fact]
    public async Task TakenAndSkipped_AreLeftAlone()
    {
        var (family, admin) = SeedFamily();
        var at = MinuteFromNow(-30);
        var course = SeedDue(admin.Id, family.Id, at);
        Db.MedicationDoses.Add(new MedicationDose
        {
            Id = Guid.NewGuid(), CourseId = course.Id, ScheduledAt = at, Units = 1, Status = DoseStatus.Taken, TakenAt = at, CreatedAt = at,
        });
        Db.SaveChanges();

        await _job.RunAsync();

        Sent().Should().BeEmpty();
        Db.MedicationDoses.AsNoTracking().Single().Status.Should().Be(DoseStatus.Taken);
    }

    [Fact]
    public async Task DoseBeforeEffectiveFrom_IsNotBackfilled()
    {
        var (family, admin) = SeedFamily();
        var at = MinuteFromNow(-30);
        SeedCourse(admin.Id, null, family.Id, admin.Id, OnceAt(at), effectiveFrom: DateTime.UtcNow);

        await _job.RunAsync();

        Db.MedicationDoses.Should().BeEmpty();
        Sent().Should().BeEmpty();
    }

    [Fact]
    public async Task PausedAndAsNeededCourses_AreIgnored()
    {
        var (family, admin) = SeedFamily();
        var at = MinuteFromNow(-1);
        SeedCourse(admin.Id, null, family.Id, admin.Id, OnceAt(at), at.AddMinutes(-1), MedicationCourseStatus.Paused);
        SeedCourse(admin.Id, null, family.Id, admin.Id, AsNeeded(), at.AddMinutes(-1));

        await _job.RunAsync();

        Db.MedicationDoses.Should().BeEmpty();
    }

    // ── Пропуск ────────────────────────────────────────────────────────────

    [Fact]
    public async Task MissedDose_NotifiesWatchers_NotOwner_AndRespectsMute()
    {
        var (family, admin) = SeedFamily();
        var watcher = AddMemberUtc(family.Id);
        var muted = AddMemberUtc(family.Id);
        Watch(admin.Id, watcher.Id);
        Watch(admin.Id, muted.Id, notifyMissed: false);
        var missedAt = MinuteFromNow(-31);
        var fresh = SeedDue(admin.Id, family.Id, missedAt);
        fresh.MissedAfterMinutes = 30; // порог 30 минут уже вышел
        Db.MedicationDoses.Add(new MedicationDose
        {
            Id = Guid.NewGuid(), CourseId = fresh.Id, ScheduledAt = missedAt, Units = 1, Status = DoseStatus.Pending,
            RemindedAt = missedAt, CreatedAt = missedAt,
        });
        Db.SaveChanges();

        await _job.RunAsync();

        Db.MedicationDoses.AsNoTracking().Single().Status.Should().Be(DoseStatus.Missed);
        var missed = Sent(NotificationType.MedicationDoseMissed);
        missed.Should().ContainSingle();
        missed.Single().UserId.Should().Be(watcher.Id);
        (missed.Single().Title + missed.Single().Body).Should().NotContain(fresh.DrugName);
        Sent(NotificationType.MedicationDoseDue).Should().BeEmpty(); // владельцу «пропущено» не шлём
    }

    [Fact]
    public async Task LongPastDose_WithoutRow_BecomesMissedImmediately_WithoutDueReminder()
    {
        var (family, admin) = SeedFamily();
        var watcher = AddMemberUtc(family.Id);
        Watch(admin.Id, watcher.Id);
        var at = MinuteFromNow(-(60 * 5)); // порог 2 часа давно вышел (например, джоба не работала)
        SeedDue(admin.Id, family.Id, at);

        await _job.RunAsync();

        Db.MedicationDoses.AsNoTracking().Single().Status.Should().Be(DoseStatus.Missed);
        Sent(NotificationType.MedicationDoseDue).Should().BeEmpty();
        Sent(NotificationType.MedicationDoseMissed).Should().ContainSingle(n => n.UserId == watcher.Id);
    }

    [Fact]
    public async Task SkippedDose_NeverNotifiesFamily()
    {
        var (family, admin) = SeedFamily();
        var watcher = AddMemberUtc(family.Id);
        Watch(admin.Id, watcher.Id);
        var at = MinuteFromNow(-(60 * 5));
        var course = SeedDue(admin.Id, family.Id, at);
        Db.MedicationDoses.Add(new MedicationDose
        {
            Id = Guid.NewGuid(), CourseId = course.Id, ScheduledAt = at, Units = 1, Status = DoseStatus.Skipped, CreatedAt = at,
        });
        Db.SaveChanges();

        await _job.RunAsync();

        Sent().Should().BeEmpty();
        Db.MedicationDoses.AsNoTracking().Single().Status.Should().Be(DoseStatus.Skipped);
    }

    [Fact]
    public async Task Missed_WatcherWhoLeftFamily_IsNotNotified()
    {
        var (family, admin) = SeedFamily();
        var watcher = AddMemberUtc(family.Id);
        Watch(admin.Id, watcher.Id);
        Db.FamilyMembers.Remove(Db.FamilyMembers.Single(m => m.UserId == watcher.Id));
        Db.SaveChanges();
        SeedDue(admin.Id, family.Id, MinuteFromNow(-(60 * 5)));

        await _job.RunAsync();

        Sent(NotificationType.MedicationDoseMissed).Should().BeEmpty();
    }

    // ── Курс подопечного ───────────────────────────────────────────────────

    [Fact]
    public async Task DependentCourse_DueGoesToReminderWatchers_WithDependentName()
    {
        var (family, admin) = SeedFamily();
        var helper = AddMemberUtc(family.Id);
        var dependent = AddDependent(family.Id, admin.Id, "Бабушка");
        var at = MinuteFromNow(-1);
        SeedCourse(null, dependent.Id, family.Id, admin.Id, OnceAt(at), at.AddMinutes(-1), name: "Аспирин");
        Db.MedicationWatchers.Add(new MedicationWatcher
        {
            Id = Guid.NewGuid(), FamilyDependentId = dependent.Id, WatcherUserId = admin.Id, ReceiveReminders = true, NotifyMissed = true, CreatedAt = DateTime.UtcNow,
        });
        Db.MedicationWatchers.Add(new MedicationWatcher
        {
            Id = Guid.NewGuid(), FamilyDependentId = dependent.Id, WatcherUserId = helper.Id, ReceiveReminders = false, NotifyMissed = true, CreatedAt = DateTime.UtcNow,
        });
        Db.SaveChanges();

        await _job.RunAsync();

        var due = Sent(NotificationType.MedicationDoseDue).Should().ContainSingle().Subject;
        due.UserId.Should().Be(admin.Id);
        due.Body.Should().Contain("Бабушка");
        (due.Title + due.Body).Should().NotContain("Аспирин");
    }

    [Fact]
    public async Task DependentCourse_MissedGoesToAllWatchers()
    {
        var (family, admin) = SeedFamily();
        var helper = AddMemberUtc(family.Id);
        var dependent = AddDependent(family.Id, admin.Id, "Бабушка");
        var at = MinuteFromNow(-(60 * 5));
        SeedCourse(null, dependent.Id, family.Id, admin.Id, OnceAt(at), at.AddMinutes(-1));
        Db.MedicationWatchers.Add(new MedicationWatcher
        {
            Id = Guid.NewGuid(), FamilyDependentId = dependent.Id, WatcherUserId = admin.Id, ReceiveReminders = true, NotifyMissed = true, CreatedAt = DateTime.UtcNow,
        });
        Db.MedicationWatchers.Add(new MedicationWatcher
        {
            Id = Guid.NewGuid(), FamilyDependentId = dependent.Id, WatcherUserId = helper.Id, ReceiveReminders = false, NotifyMissed = true, CreatedAt = DateTime.UtcNow,
        });
        Db.SaveChanges();

        await _job.RunAsync();

        Sent(NotificationType.MedicationDoseMissed).Select(n => n.UserId).Should().BeEquivalentTo([admin.Id, helper.Id]);
    }

    // ── Часовая задача ─────────────────────────────────────────────────────

    [Fact]
    public async Task LowStock_NotifiesOnce_UntilRestocked()
    {
        var (family, admin) = SeedFamily();
        var med = AddMedication(family.Id, admin.Id, "6 таб.");
        var twice = new DoseSchedule(DoseScheduleMode.TimesPerDay,
            Times: [new DoseTime(new TimeOnly(8, 0), 1), new DoseTime(new TimeOnly(20, 0), 1)]);
        var course = SeedCourse(admin.Id, null, family.Id, admin.Id, twice, medicationId: med.Id, writeOff: true);

        await _maintenance.RunAsync();
        await _maintenance.RunAsync();

        var low = Sent(NotificationType.MedicationStockLow).Should().ContainSingle().Subject;
        low.RelatedEntityId.Should().Be(course.Id);
        low.RelatedEntityKind.Should().Be(NotificationRelatedKind.MedicationCourse);
        (low.Title + low.Body).Should().NotContain(course.DrugName);
        Db.MedicationCourses.AsNoTracking().Single().LowStockNotifiedAt.Should().NotBeNull();

        // Запас пополнили — флаг сбрасывается, следующая нехватка снова уведомит.
        Db.Medications.Where(m => m.Id == med.Id).ExecuteUpdate(s => s.SetProperty(m => m.DataJson, "{\"quantity\":\"60 таб.\"}"));
        await _maintenance.RunAsync();
        Db.MedicationCourses.AsNoTracking().Single().LowStockNotifiedAt.Should().BeNull();
    }

    [Fact]
    public async Task EnoughStock_NoWarning()
    {
        var (family, admin) = SeedFamily();
        var med = AddMedication(family.Id, admin.Id, "60 таб.");
        SeedCourse(admin.Id, null, family.Id, admin.Id, Request().Schedule, medicationId: med.Id, writeOff: true);

        await _maintenance.RunAsync();

        Sent().Should().BeEmpty();
    }

    [Fact]
    public async Task CourseAfterEndDate_IsCompleted_UnlessDosesUnresolved()
    {
        var (family, admin) = SeedFamily();
        var done = SeedCourse(admin.Id, null, family.Id, admin.Id, AsNeeded(), name: "Готов");
        done.EndDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2);
        var pending = SeedCourse(admin.Id, null, family.Id, admin.Id, AsNeeded(), name: "Ждёт");
        pending.EndDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2);
        var running = SeedCourse(admin.Id, null, family.Id, admin.Id, AsNeeded(), name: "Идёт");
        running.EndDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3);
        Db.MedicationDoses.Add(new MedicationDose
        {
            Id = Guid.NewGuid(), CourseId = pending.Id, ScheduledAt = DateTime.UtcNow.AddDays(-2), Units = 1,
            Status = DoseStatus.Pending, CreatedAt = DateTime.UtcNow,
        });
        Db.SaveChanges();

        await _maintenance.RunAsync();

        var statuses = Db.MedicationCourses.AsNoTracking().ToDictionary(c => c.DrugName, c => c.Status);
        statuses["Готов"].Should().Be(MedicationCourseStatus.Completed);
        statuses["Ждёт"].Should().Be(MedicationCourseStatus.Active);
        statuses["Идёт"].Should().Be(MedicationCourseStatus.Active);
    }

    [Fact]
    public async Task ExpiredTokens_ArePurged()
    {
        var (family, admin) = SeedFamily();
        var course = SeedCourse(admin.Id, null, family.Id, admin.Id, AsNeeded());
        var dose = new MedicationDose
        {
            Id = Guid.NewGuid(), CourseId = course.Id, ScheduledAt = DateTime.UtcNow, Units = 1, Status = DoseStatus.Pending, CreatedAt = DateTime.UtcNow,
        };
        Db.MedicationDoses.Add(dose);
        Db.DoseActionTokens.Add(new DoseActionToken
        {
            Id = Guid.NewGuid(), TokenHash = "old", DoseId = dose.Id, RecipientUserId = admin.Id, ExpiresAt = DateTime.UtcNow.AddDays(-3), CreatedAt = DateTime.UtcNow.AddDays(-4),
        });
        Db.DoseActionTokens.Add(new DoseActionToken
        {
            Id = Guid.NewGuid(), TokenHash = "fresh", DoseId = dose.Id, RecipientUserId = admin.Id, ExpiresAt = DateTime.UtcNow.AddHours(3), CreatedAt = DateTime.UtcNow,
        });
        Db.SaveChanges();

        await _maintenance.RunAsync();

        Db.DoseActionTokens.AsNoTracking().Select(t => t.TokenHash).Should().Equal("fresh");
    }
}
