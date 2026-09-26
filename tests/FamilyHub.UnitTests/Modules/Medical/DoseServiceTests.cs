using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.MedicationCourses;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical;

public class DoseServiceTests : MedicationCourseTestBase
{
    /// <summary>Курс «раз в день через полчаса» с привязанной аптечкой — приём можно отметить сразу (заранее ≤ 3 ч).</summary>
    private (User User, MedicationCourse Course, Medication Med, DateTime At) SeedOwnCourse(string? quantity = "22 таб.")
    {
        var (family, admin) = SeedFamily();
        var med = AddMedication(family.Id, admin.Id, quantity);
        var at = MinuteFromNow(30);
        var course = SeedCourse(admin.Id, null, family.Id, admin.Id, OnceAt(at), DateTime.UtcNow.AddMinutes(-1),
            medicationId: med.Id, writeOff: true);
        return (admin, course, med, at);
    }

    [Fact]
    public async Task Taken_WritesOffStock_AndCreatesDiaryNote()
    {
        var (user, course, med, at) = SeedOwnCourse();

        var (result, dose, _) = await Doses.ApplyAsync(user.Id, course.Id, at, DoseAction.Taken, null);

        result.Should().Be(DoseResult.Success);
        dose!.Status.Should().Be(DoseStatus.Taken);
        dose.StockWrittenOff.Should().BeTrue();
        QuantityOf(med.Id).Should().Be("21 таб.");

        var note = Db.HealthNotes.Should().ContainSingle().Subject;
        note.Kind.Should().Be(HealthNoteKind.MedicationIntake);
        note.OwnerUserId.Should().Be(user.Id);
        note.Title.Should().Be("Сорбифер");
        Db.MedicationDoses.Single().HealthNoteId.Should().Be(note.Id);
    }

    [Fact]
    public async Task Taken_Twice_IsIdempotent_DoesNotDoubleWriteOff()
    {
        var (user, course, med, at) = SeedOwnCourse();

        await Doses.ApplyAsync(user.Id, course.Id, at, DoseAction.Taken, null);
        var (result, _, _) = await Doses.ApplyAsync(user.Id, course.Id, at, DoseAction.Taken, null);

        result.Should().Be(DoseResult.Success);
        QuantityOf(med.Id).Should().Be("21 таб.");
        Db.HealthNotes.Should().ContainSingle();
        Db.MedicationDoses.Should().ContainSingle();
    }

    [Fact]
    public async Task Taken_WithoutParseableQuantity_StillMarksDose_NoWriteOff()
    {
        var (user, course, med, at) = SeedOwnCourse(quantity: "много");

        var (result, dose, _) = await Doses.ApplyAsync(user.Id, course.Id, at, DoseAction.Taken, null);

        result.Should().Be(DoseResult.Success);
        dose!.StockWrittenOff.Should().BeFalse();
        QuantityOf(med.Id).Should().Be("много");
    }

    [Fact]
    public async Task Taken_StockNeverGoesBelowZero()
    {
        var (user, course, med, at) = SeedOwnCourse(quantity: "0 таб.");

        var (_, dose, _) = await Doses.ApplyAsync(user.Id, course.Id, at, DoseAction.Taken, null);

        QuantityOf(med.Id).Should().Be("0 таб.");
        dose!.StockWrittenOff.Should().BeFalse();
    }

    [Fact]
    public async Task Taken_ForDependent_WritesOffStock_ButNoDiaryNote()
    {
        var (family, admin) = SeedFamily();
        var dependent = AddDependent(family.Id, admin.Id);
        var med = AddMedication(family.Id, admin.Id);
        var at = MinuteFromNow(30);
        var course = SeedCourse(null, dependent.Id, family.Id, admin.Id, OnceAt(at), DateTime.UtcNow.AddMinutes(-1),
            medicationId: med.Id, writeOff: true);
        var otherMember = AddMemberUtc(family.Id);

        var (result, _, _) = await Doses.ApplyAsync(otherMember.Id, course.Id, at, DoseAction.Taken, null);

        result.Should().Be(DoseResult.Success);
        QuantityOf(med.Id).Should().Be("21 таб.");
        Db.HealthNotes.Should().BeEmpty(); // дневник строго личный
        Db.MedicationDoses.Single().ActedByUserId.Should().Be(otherMember.Id);
    }

    [Fact]
    public async Task Undo_RestoresStock_RemovesDiaryNote_AndResetsDose()
    {
        var (user, course, med, at) = SeedOwnCourse();
        var (_, dose, _) = await Doses.ApplyAsync(user.Id, course.Id, at, DoseAction.Taken, null);

        var result = await Doses.UndoAsync(user.Id, dose!.Id);

        result.Should().Be(DoseResult.Success);
        QuantityOf(med.Id).Should().Be("22 таб.");
        Db.HealthNotes.Should().BeEmpty();
        var row = Db.MedicationDoses.AsNoTracking().Single();
        row.Status.Should().Be(DoseStatus.Pending);
        row.TakenAt.Should().BeNull();
        row.WriteOffUnits.Should().BeNull();
    }

    [Fact]
    public async Task Undo_OfPendingDose_IsConflict()
    {
        var (user, course, _, at) = SeedOwnCourse();
        var (_, dose, _) = await Doses.ApplyAsync(user.Id, course.Id, at, DoseAction.Snooze10, null);

        (await Doses.UndoAsync(user.Id, dose!.Id)).Should().Be(DoseResult.Conflict);
    }

    [Fact]
    public async Task Snooze_SetsSnoozedUntil_AndCountsSnoozes()
    {
        var (user, course, _, at) = SeedOwnCourse();

        var (result, dose, _) = await Doses.ApplyAsync(user.Id, course.Id, at, DoseAction.Snooze30, null);

        result.Should().Be(DoseResult.Success);
        dose!.Status.Should().Be(DoseStatus.Snoozed);
        dose.SnoozedUntil.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(30), TimeSpan.FromSeconds(10));
        (await Doses.ApplyAsync(user.Id, course.Id, at, DoseAction.Snooze10, null)).Result.Should().Be(DoseResult.Success);
        Db.MedicationDoses.AsNoTracking().Single().SnoozeCount.Should().Be(2);
    }

    [Fact]
    public async Task Snooze_AfterTaken_IsConflict()
    {
        var (user, course, _, at) = SeedOwnCourse();
        await Doses.ApplyAsync(user.Id, course.Id, at, DoseAction.Taken, null);

        (await Doses.ApplyAsync(user.Id, course.Id, at, DoseAction.Snooze10, null)).Result.Should().Be(DoseResult.Conflict);
    }

    [Fact]
    public async Task Skip_MarksSkipped_NoStockOrDiary()
    {
        var (user, course, med, at) = SeedOwnCourse();

        var (result, dose, _) = await Doses.ApplyAsync(user.Id, course.Id, at, DoseAction.Skip, null);

        result.Should().Be(DoseResult.Success);
        dose!.Status.Should().Be(DoseStatus.Skipped);
        QuantityOf(med.Id).Should().Be("22 таб.");
        Db.HealthNotes.Should().BeEmpty();
    }

    [Fact]
    public async Task Skip_AfterTaken_IsConflict()
    {
        var (user, course, _, at) = SeedOwnCourse();
        await Doses.ApplyAsync(user.Id, course.Id, at, DoseAction.Taken, null);

        (await Doses.ApplyAsync(user.Id, course.Id, at, DoseAction.Skip, null)).Result.Should().Be(DoseResult.Conflict);
    }

    [Fact]
    public async Task Watcher_CannotAct_OnAdultsCourse()
    {
        var (user, course, _, at) = SeedOwnCourse();
        var watcher = AddMemberUtc(Db.FamilyMembers.First(m => m.UserId == user.Id).FamilyId);
        Db.MedicationWatchers.Add(new MedicationWatcher
        {
            Id = Guid.NewGuid(), SubjectUserId = user.Id, WatcherUserId = watcher.Id, NotifyMissed = true, CreatedAt = DateTime.UtcNow,
        });
        Db.SaveChanges();

        (await Doses.ApplyAsync(watcher.Id, course.Id, at, DoseAction.Taken, null)).Result.Should().Be(DoseResult.Forbidden);
    }

    [Fact]
    public async Task Stranger_GetsNotFound()
    {
        var (_, course, _, at) = SeedOwnCourse();
        var stranger = Db.AddUser();

        (await Doses.ApplyAsync(stranger.Id, course.Id, at, DoseAction.Taken, null)).Result.Should().Be(DoseResult.NotFound);
    }

    [Fact]
    public async Task TimeNotInSchedule_IsInvalid()
    {
        var (user, course, _, at) = SeedOwnCourse();

        (await Doses.ApplyAsync(user.Id, course.Id, at.AddMinutes(7), DoseAction.Taken, null)).Result.Should().Be(DoseResult.Invalid);
    }

    [Fact]
    public async Task FarFutureDose_IsInvalid()
    {
        var (family, admin) = SeedFamily();
        var at = MinuteFromNow(60 * 6);
        var course = SeedCourse(admin.Id, null, family.Id, admin.Id, OnceAt(at), DateTime.UtcNow.AddMinutes(-1));

        (await Doses.ApplyAsync(admin.Id, course.Id, at, DoseAction.Taken, null)).Result.Should().Be(DoseResult.Invalid);
    }

    [Fact]
    public async Task DoseBeforeEffectiveFrom_IsInvalid()
    {
        var (family, admin) = SeedFamily();
        var at = MinuteFromNow(30);
        var course = SeedCourse(admin.Id, null, family.Id, admin.Id, OnceAt(at), effectiveFrom: DateTime.UtcNow.AddHours(2));

        (await Doses.ApplyAsync(admin.Id, course.Id, at, DoseAction.Taken, null)).Result.Should().Be(DoseResult.Invalid);
    }

    [Theory]
    [InlineData(MedicationCourseStatus.Paused)]
    [InlineData(MedicationCourseStatus.Completed)]
    public async Task InactiveCourse_IsConflict(MedicationCourseStatus status)
    {
        var (family, admin) = SeedFamily();
        var at = MinuteFromNow(30);
        var course = SeedCourse(admin.Id, null, family.Id, admin.Id, OnceAt(at), DateTime.UtcNow.AddMinutes(-1), status);

        (await Doses.ApplyAsync(admin.Id, course.Id, at, DoseAction.Taken, null)).Result.Should().Be(DoseResult.Conflict);
    }

    [Fact]
    public async Task ApplyToDose_ByExistingRow_MarksTaken_ForPushButtons()
    {
        var (user, course, med, at) = SeedOwnCourse();
        var (_, dose, _) = await Doses.ApplyAsync(user.Id, course.Id, at, DoseAction.Snooze10, null);

        var (result, taken, _) = await Doses.ApplyToDoseAsync(user.Id, dose!.Id, DoseAction.Taken);

        result.Should().Be(DoseResult.Success);
        taken!.Status.Should().Be(DoseStatus.Taken);
        QuantityOf(med.Id).Should().Be("21 таб.");
    }

    [Fact]
    public async Task Taken_MarksDueNotificationsRead()
    {
        var (user, course, _, at) = SeedOwnCourse();
        var (_, dose, _) = await Doses.ApplyAsync(user.Id, course.Id, at, DoseAction.Snooze10, null);
        var n = new Notification
        {
            Id = Guid.NewGuid(), UserId = user.Id, Type = NotificationType.MedicationDoseDue, Title = "t", Body = "b",
            RelatedEntityId = dose!.Id, DedupKey = "k1", CreatedAt = DateTime.UtcNow,
        };
        Db.Notifications.Add(n);
        Db.SaveChanges();

        await Doses.ApplyToDoseAsync(user.Id, dose.Id, DoseAction.Taken);

        Db.Notifications.AsNoTracking().Single(x => x.Id == n.Id).IsRead.Should().BeTrue();
    }

    // ── «По необходимости» ────────────────────────────────────────────────

    private (User User, MedicationCourse Course) SeedAsNeeded(int max = 2)
    {
        var (family, admin) = SeedFamily();
        var course = SeedCourse(admin.Id, null, family.Id, admin.Id, AsNeeded(max), DateTime.UtcNow.AddMinutes(-1), name: "Нурофен");
        return (admin, course);
    }

    [Fact]
    public async Task AsNeeded_Take_CreatesTakenDose_AndDiaryNote()
    {
        var (user, course) = SeedAsNeeded();

        var (result, dose, _) = await Doses.TakeAsNeededAsync(user.Id, course.Id, new PrnRequest(null, false));

        result.Should().Be(DoseResult.Success);
        dose!.ScheduledAt.Should().BeNull();
        dose.Status.Should().Be(DoseStatus.Taken);
        Db.HealthNotes.Should().ContainSingle(n => n.Title == "Нурофен");
    }

    [Fact]
    public async Task AsNeeded_OverDailyLimit_NeedsConfirmation()
    {
        var (user, course) = SeedAsNeeded(max: 1);
        await Doses.TakeAsNeededAsync(user.Id, course.Id, new PrnRequest(null, false));

        var (result, _, _) = await Doses.TakeAsNeededAsync(user.Id, course.Id, new PrnRequest(null, false));
        result.Should().Be(DoseResult.OverLimit);
        Db.MedicationDoses.Should().ContainSingle();

        (await Doses.TakeAsNeededAsync(user.Id, course.Id, new PrnRequest(null, Force: true))).Result.Should().Be(DoseResult.Success);
        Db.MedicationDoses.Should().HaveCount(2);
    }

    [Fact]
    public async Task AsNeeded_OnScheduledCourse_IsInvalid()
    {
        var (user, course, _, _) = SeedOwnCourse();

        (await Doses.TakeAsNeededAsync(user.Id, course.Id, new PrnRequest(null, false))).Result.Should().Be(DoseResult.Invalid);
    }

    [Fact]
    public async Task AsNeeded_Undo_RemovesTheDose()
    {
        var (user, course) = SeedAsNeeded();
        var (_, dose, _) = await Doses.TakeAsNeededAsync(user.Id, course.Id, new PrnRequest(null, false));

        (await Doses.UndoAsync(user.Id, dose!.Id)).Should().Be(DoseResult.Success);

        Db.MedicationDoses.Should().BeEmpty();
        Db.HealthNotes.Should().BeEmpty();
    }
}
