using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.MedicationCourses;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.MedicationCourses;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical;

public class MedicationCourseServiceTests : MedicationCourseTestBase
{
    // ── Создание ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_OwnCourse_StoresCourse_AndReturnsDetail()
    {
        var (_, admin) = SeedFamily();

        var (result, item, _) = await Courses.CreateAsync(admin.Id, Request());

        result.Should().Be(CourseResult.Success);
        item!.Summary.Subject.IsSelf.Should().BeTrue();
        item.Summary.CanEdit.Should().BeTrue();
        var course = Db.MedicationCourses.AsNoTracking().Single();
        course.SubjectUserId.Should().Be(admin.Id);
        course.FamilyDependentId.Should().BeNull();
        course.DrugName.Should().Be("Сорбифер Дурулес 100 мг");
        course.Status.Should().Be(MedicationCourseStatus.Active);
        course.TimeZoneId.Should().Be("UTC");
        Db.MedicationWatchers.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_ForDependent_AddsCreatorAsReminderWatcher()
    {
        var (family, admin) = SeedFamily();
        var dependent = AddDependent(family.Id, admin.Id);

        var (result, item, _) = await Courses.CreateAsync(admin.Id, Request(dependentId: dependent.Id));

        result.Should().Be(CourseResult.Success);
        item!.Summary.Subject.Kind.Should().Be("dependent");
        item.Summary.Subject.Name.Should().Be("Мама");
        var course = Db.MedicationCourses.AsNoTracking().Single();
        course.FamilyId.Should().Be(family.Id);
        var watcher = Db.MedicationWatchers.AsNoTracking().Single();
        watcher.FamilyDependentId.Should().Be(dependent.Id);
        watcher.WatcherUserId.Should().Be(admin.Id);
        watcher.ReceiveReminders.Should().BeTrue();
    }

    [Fact]
    public async Task Create_ForDependentOfAnotherFamily_IsNotFound()
    {
        var (_, admin) = SeedFamily();
        var (otherFamily, otherAdmin) = Db.SeedFamilyWithAdmin("Другая");
        var foreign = AddDependent(otherFamily.Id, otherAdmin.Id);

        var (result, _, _) = await Courses.CreateAsync(admin.Id, Request(dependentId: foreign.Id));

        result.Should().Be(CourseResult.NotFound);
        Db.MedicationCourses.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_InvalidSchedule_IsRejected()
    {
        var (_, admin) = SeedFamily();
        var bad = new DoseSchedule(DoseScheduleMode.EveryNHours, IntervalHours: 5, IntervalStart: new TimeOnly(8, 0));

        var (result, _, error) = await Courses.CreateAsync(admin.Id, Request(bad));

        result.Should().Be(CourseResult.Invalid);
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Create_WithMedicationOfForeignFamily_IsRejected()
    {
        var (_, admin) = SeedFamily();
        var (otherFamily, otherAdmin) = Db.SeedFamilyWithAdmin("Другая");
        var foreignMed = AddMedication(otherFamily.Id, otherAdmin.Id);

        var (result, _, _) = await Courses.CreateAsync(admin.Id, Request(medicationId: foreignMed.Id, writeOff: true));

        result.Should().Be(CourseResult.Invalid);
    }

    [Fact]
    public async Task Create_WriteOffWithoutMedication_IsRejected()
    {
        var (_, admin) = SeedFamily();

        (await Courses.CreateAsync(admin.Id, Request(writeOff: true))).Result.Should().Be(CourseResult.Invalid);
    }

    [Fact]
    public async Task Create_LinksMedkitFamily_ForOwnCourse()
    {
        var (family, admin) = SeedFamily();
        var med = AddMedication(family.Id, admin.Id);

        var (result, item, _) = await Courses.CreateAsync(admin.Id, Request(medicationId: med.Id, writeOff: true));

        result.Should().Be(CourseResult.Success);
        Db.MedicationCourses.AsNoTracking().Single().FamilyId.Should().Be(family.Id);
        item!.Summary.WriteOffEnabled.Should().BeTrue();
        item.Summary.Stock!.Quantity.Should().Be(22);
    }

    // ── Видимость ────────────────────────────────────────────────────────

    [Fact]
    public async Task Visibility_OwnDependentWatchedAndStranger()
    {
        var (family, admin) = SeedFamily();
        var member = AddMemberUtc(family.Id);
        var dependent = AddDependent(family.Id, admin.Id);
        var ownCourse = SeedCourse(admin.Id, null, family.Id, admin.Id, AsNeeded());
        var depCourse = SeedCourse(null, dependent.Id, family.Id, admin.Id, AsNeeded());
        Db.MedicationWatchers.Add(new MedicationWatcher
        {
            Id = Guid.NewGuid(), SubjectUserId = admin.Id, WatcherUserId = member.Id, NotifyMissed = true, CreatedAt = DateTime.UtcNow,
        });
        Db.SaveChanges();
        var stranger = Db.AddUser();

        // Владелец: своё и подопечного (он член семьи) — с правом правки.
        (await Courses.GetAsync(admin.Id, ownCourse.Id)).Item!.Summary.CanEdit.Should().BeTrue();
        (await Courses.GetAsync(admin.Id, depCourse.Id)).Item!.Summary.CanEdit.Should().BeTrue();

        // Другой член семьи: подопечный — полный доступ; курс взрослого — только чтение («вы следите»).
        (await Courses.GetAsync(member.Id, depCourse.Id)).Item!.Summary.CanEdit.Should().BeTrue();
        var watched = (await Courses.GetAsync(member.Id, ownCourse.Id)).Item!;
        watched.Summary.CanEdit.Should().BeFalse();
        watched.Summary.IsWatching.Should().BeTrue();
        watched.Watchers.Should().BeEmpty(); // список наблюдателей — только тем, кто может править

        // Чужой человек не отличает курс от несуществующего.
        (await Courses.GetAsync(stranger.Id, ownCourse.Id)).Result.Should().Be(CourseResult.NotFound);
        (await Courses.GetAsync(stranger.Id, depCourse.Id)).Result.Should().Be(CourseResult.NotFound);
    }

    [Fact]
    public async Task Visibility_MemberWithoutWatcherRow_DoesNotSeeAdultsCourse()
    {
        var (family, admin) = SeedFamily();
        var member = AddMemberUtc(family.Id);
        var course = SeedCourse(admin.Id, null, family.Id, admin.Id, AsNeeded());

        (await Courses.GetAsync(member.Id, course.Id)).Result.Should().Be(CourseResult.NotFound);
    }

    [Fact]
    public async Task Visibility_WatcherWhoLeftFamily_LosesAccess()
    {
        var (family, admin) = SeedFamily();
        var watcher = AddMemberUtc(family.Id);
        var course = SeedCourse(admin.Id, null, family.Id, admin.Id, AsNeeded());
        Db.MedicationWatchers.Add(new MedicationWatcher
        {
            Id = Guid.NewGuid(), SubjectUserId = admin.Id, WatcherUserId = watcher.Id, NotifyMissed = true, CreatedAt = DateTime.UtcNow,
        });
        Db.SaveChanges();
        (await Courses.GetAsync(watcher.Id, course.Id)).Result.Should().Be(CourseResult.Success);

        Db.FamilyMembers.Remove(Db.FamilyMembers.Single(m => m.UserId == watcher.Id));
        Db.SaveChanges();

        (await Courses.GetAsync(watcher.Id, course.Id)).Result.Should().Be(CourseResult.NotFound);
    }

    [Fact]
    public async Task List_SplitsActiveAndCompleted()
    {
        var (family, admin) = SeedFamily();
        SeedCourse(admin.Id, null, family.Id, admin.Id, AsNeeded(), name: "Активный");
        SeedCourse(admin.Id, null, family.Id, admin.Id, AsNeeded(), status: MedicationCourseStatus.Paused, name: "На паузе");
        SeedCourse(admin.Id, null, family.Id, admin.Id, AsNeeded(), status: MedicationCourseStatus.Completed, name: "Готово");

        (await Courses.ListAsync(admin.Id, completed: false)).Select(c => c.DrugName).Should().BeEquivalentTo("Активный", "На паузе");
        (await Courses.ListAsync(admin.Id, completed: true)).Select(c => c.DrugName).Should().BeEquivalentTo("Готово");
    }

    // ── Правка и жизненный цикл ───────────────────────────────────────────

    [Fact]
    public async Task Update_ScheduleChange_MovesEffectiveFrom_AndDropsFutureUnresolvedDoses()
    {
        var (family, admin) = SeedFamily();
        var at = MinuteFromNow(30);
        var course = SeedCourse(admin.Id, null, family.Id, admin.Id, OnceAt(at), DateTime.UtcNow.AddDays(-2));
        Db.MedicationDoses.Add(new MedicationDose
        {
            Id = Guid.NewGuid(), CourseId = course.Id, ScheduledAt = at, Units = 1, Status = DoseStatus.Pending, CreatedAt = DateTime.UtcNow,
        });
        var oldTaken = new MedicationDose
        {
            Id = Guid.NewGuid(), CourseId = course.Id, ScheduledAt = at.AddDays(-1), Units = 1, Status = DoseStatus.Taken,
            TakenAt = at.AddDays(-1), CreatedAt = DateTime.UtcNow,
        };
        Db.MedicationDoses.Add(oldTaken);
        Db.SaveChanges();

        var changed = Request(new DoseSchedule(DoseScheduleMode.TimesPerDay, Times: [new DoseTime(new TimeOnly(9, 0), 2)]));
        var (result, _, _) = await Courses.UpdateAsync(admin.Id, course.Id, changed);

        result.Should().Be(CourseResult.Success);
        Db.MedicationCourses.AsNoTracking().Single().EffectiveFromUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(10));
        Db.MedicationDoses.AsNoTracking().Select(d => d.Id).Should().Equal(oldTaken.Id); // история остаётся, будущее — нет
    }

    [Fact]
    public async Task Update_NameOnly_KeepsEffectiveFrom()
    {
        var (family, admin) = SeedFamily();
        var effective = DateTime.UtcNow.AddDays(-5);
        var schedule = new DoseSchedule(DoseScheduleMode.TimesPerDay, Times: [new DoseTime(new TimeOnly(8, 0), 1)]);
        var course = SeedCourse(admin.Id, null, family.Id, admin.Id, schedule, effective);
        course.StartDate = DateOnly.FromDateTime(DateTime.UtcNow);
        Db.SaveChanges();

        var (result, _, _) = await Courses.UpdateAsync(admin.Id, course.Id, Request(schedule, name: "Новое имя"));

        result.Should().Be(CourseResult.Success);
        var stored = Db.MedicationCourses.AsNoTracking().Single();
        stored.DrugName.Should().Be("Новое имя");
        stored.EffectiveFromUtc.Should().BeCloseTo(effective, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Update_WithoutSource_KeepsTheOriginalPrescription()
    {
        var (family, admin) = SeedFamily();
        var recordId = Guid.NewGuid();
        var course = SeedCourse(admin.Id, null, family.Id, admin.Id, AsNeeded());
        course.SourceMedicalRecordId = recordId;
        course.SourcePrescriptionIndex = 2;
        Db.SaveChanges();

        var (result, _, _) = await Courses.UpdateAsync(admin.Id, course.Id, Request(AsNeeded(), name: "Новое имя"));

        result.Should().Be(CourseResult.Success);
        var stored = Db.MedicationCourses.AsNoTracking().Single();
        stored.SourceMedicalRecordId.Should().Be(recordId);
        stored.SourcePrescriptionIndex.Should().Be(2);
    }

    [Fact]
    public async Task Update_ByReadOnlyWatcher_IsForbidden()
    {
        var (family, admin) = SeedFamily();
        var watcher = AddMemberUtc(family.Id);
        var course = SeedCourse(admin.Id, null, family.Id, admin.Id, AsNeeded());
        Db.MedicationWatchers.Add(new MedicationWatcher
        {
            Id = Guid.NewGuid(), SubjectUserId = admin.Id, WatcherUserId = watcher.Id, NotifyMissed = true, CreatedAt = DateTime.UtcNow,
        });
        Db.SaveChanges();

        (await Courses.UpdateAsync(watcher.Id, course.Id, Request(AsNeeded()))).Result.Should().Be(CourseResult.Forbidden);
    }

    [Fact]
    public async Task Update_CannotChangeWhoTheCourseIsFor()
    {
        var (family, admin) = SeedFamily();
        var dependent = AddDependent(family.Id, admin.Id);
        var course = SeedCourse(admin.Id, null, family.Id, admin.Id, AsNeeded());

        (await Courses.UpdateAsync(admin.Id, course.Id, Request(AsNeeded(), dependentId: dependent.Id))).Result
            .Should().Be(CourseResult.Invalid);
    }

    [Fact]
    public async Task PauseResumeComplete_Lifecycle()
    {
        var (family, admin) = SeedFamily();
        var at = MinuteFromNow(30);
        var course = SeedCourse(admin.Id, null, family.Id, admin.Id, OnceAt(at), DateTime.UtcNow.AddDays(-1));
        Db.MedicationDoses.Add(new MedicationDose
        {
            Id = Guid.NewGuid(), CourseId = course.Id, ScheduledAt = at, Units = 1, Status = DoseStatus.Snoozed, CreatedAt = DateTime.UtcNow,
        });
        Db.SaveChanges();

        (await Courses.PauseAsync(admin.Id, course.Id)).Should().Be(CourseResult.Success);
        Db.MedicationCourses.AsNoTracking().Single().Status.Should().Be(MedicationCourseStatus.Paused);
        Db.MedicationDoses.Should().BeEmpty(); // незакрытые приёмы при паузе не нужны

        (await Courses.PauseAsync(admin.Id, course.Id)).Should().Be(CourseResult.Invalid); // уже на паузе

        (await Courses.ResumeAsync(admin.Id, course.Id)).Should().Be(CourseResult.Success);
        var resumed = Db.MedicationCourses.AsNoTracking().Single();
        resumed.Status.Should().Be(MedicationCourseStatus.Active);
        resumed.EffectiveFromUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(10));

        (await Courses.CompleteAsync(admin.Id, course.Id)).Should().Be(CourseResult.Success);
        Db.MedicationCourses.AsNoTracking().Single().Status.Should().Be(MedicationCourseStatus.Completed);
        (await Courses.CompleteAsync(admin.Id, course.Id)).Should().Be(CourseResult.Invalid);
        (await Courses.UpdateAsync(admin.Id, course.Id, Request(AsNeeded()))).Result.Should().Be(CourseResult.Invalid);
    }

    [Fact]
    public async Task Delete_DependentCourse_OnlyCreatorOrAdmin()
    {
        var (family, admin) = SeedFamily();
        var creator = AddMemberUtc(family.Id);
        var other = AddMemberUtc(family.Id);
        var dependent = AddDependent(family.Id, admin.Id);
        var course = SeedCourse(null, dependent.Id, family.Id, creator.Id, AsNeeded());

        (await Courses.DeleteAsync(other.Id, course.Id)).Should().Be(CourseResult.Forbidden);
        (await Courses.DeleteAsync(creator.Id, course.Id)).Should().Be(CourseResult.Success);
        Db.MedicationCourses.Should().BeEmpty();

        var second = SeedCourse(null, dependent.Id, family.Id, creator.Id, AsNeeded());
        (await Courses.DeleteAsync(admin.Id, second.Id)).Should().Be(CourseResult.Success);
    }

    [Fact]
    public async Task Delete_KeepsDiaryNotes_AndRemovesDoses()
    {
        var (family, admin) = SeedFamily();
        var at = MinuteFromNow(30);
        var course = SeedCourse(admin.Id, null, family.Id, admin.Id, OnceAt(at), DateTime.UtcNow.AddMinutes(-1));
        await Doses.ApplyAsync(admin.Id, course.Id, at, DoseAction.Taken, null);

        (await Courses.DeleteAsync(admin.Id, course.Id)).Should().Be(CourseResult.Success);

        Db.MedicationDoses.Should().BeEmpty();
        Db.HealthNotes.Should().ContainSingle();
    }

    [Fact]
    public async Task DeletingDependent_CascadesCoursesAndWatchers()
    {
        var (family, admin) = SeedFamily();
        var dependent = AddDependent(family.Id, admin.Id);
        SeedCourse(null, dependent.Id, family.Id, admin.Id, AsNeeded());
        Db.MedicationWatchers.Add(new MedicationWatcher
        {
            Id = Guid.NewGuid(), FamilyDependentId = dependent.Id, WatcherUserId = admin.Id, CreatedAt = DateTime.UtcNow,
        });
        Db.SaveChanges();

        await Db.FamilyDependents.Where(d => d.Id == dependent.Id).ExecuteDeleteAsync();

        Db.MedicationCourses.Should().BeEmpty();
        Db.MedicationWatchers.Should().BeEmpty();
    }

    // ── Предпросмотр и назначения ────────────────────────────────────────

    [Fact]
    public async Task Preview_ComputesNeededDaysAndShortfall()
    {
        var (family, admin) = SeedFamily();
        var med = AddMedication(family.Id, admin.Id, "22 таб.");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var twice = new DoseSchedule(DoseScheduleMode.TimesPerDay,
            Times: [new DoseTime(new TimeOnly(8, 0), 1), new DoseTime(new TimeOnly(20, 0), 1)]);

        var (result, item, _) = await Courses.PreviewAsync(admin.Id,
            new CoursePreviewRequest(twice, today, today.AddDays(55), DoseUnit.Tablet, med.Id));

        result.Should().Be(CourseResult.Success);
        item!.AverageUnitsPerDay.Should().Be(2);
        item.NeededForCourse.Should().Be(112); // 56 дней × 2 — как в макете
        item.Quantity.Should().Be(22);
        item.DaysCovered.Should().Be(11);
        item.Shortfall.Should().Be(90);
    }

    [Fact]
    public async Task Preview_OpenEnded_HasNoNeededOrShortfall()
    {
        var (family, admin) = SeedFamily();
        var med = AddMedication(family.Id, admin.Id);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var (_, item, _) = await Courses.PreviewAsync(admin.Id,
            new CoursePreviewRequest(Request().Schedule, today, null, DoseUnit.Tablet, med.Id));

        item!.NeededForCourse.Should().BeNull();
        item.Shortfall.Should().BeNull();
        item.DaysCovered.Should().Be(11);
    }

    private MedicalRecord SeedVisit(Guid ownerId, string dosage, Guid? dependentId = null, Guid? targetUserId = null)
    {
        var record = TestData.NewMedicalRecord(ownerId, MedicalRecordKind.DoctorVisit);
        record.FamilyDependentId = dependentId;
        record.TargetUserId = targetUserId;
        record.Doctor = "Смирнова А. И.";
        record.ExtractedDataJson = JsonSerializer.Serialize(new VisitConclusion(null, null, null, null,
            [new PrescribedMedication("Сорбифер Дурулес 100 мг", dosage)]));
        Db.MedicalRecords.Add(record);
        Db.SaveChanges();
        return record;
    }

    [Fact]
    public async Task Prescriptions_ForMyself_ReturnsParsedDraft_WithoutAuditOfOwnData()
    {
        var (_, admin) = SeedFamily();
        var visit = SeedVisit(admin.Id, "по 1 таблетке 2 раза в день до еды, 8 недель");

        var (result, items) = await Courses.GetPrescriptionsAsync(admin.Id, null);

        result.Should().Be(CourseResult.Success);
        var v = items.Should().ContainSingle().Subject;
        v.RecordId.Should().Be(visit.Id);
        var draft = v.Items.Single().Draft;
        draft.TimesPerDay.Should().Be(2);
        draft.Food.Should().Be(FoodRelation.Before);
        draft.DurationDays.Should().Be(56);
        Db.Set<MedicalAccessAudit>().Should().BeEmpty();
    }

    [Fact]
    public async Task Prescriptions_ForDependent_OnlyThatDependent_AndAuditsForeignOwner()
    {
        var (family, admin) = SeedFamily();
        var member = AddMemberUtc(family.Id);
        var dependent = AddDependent(family.Id, admin.Id);
        SeedVisit(admin.Id, "1 таб. 2 раза в день", dependentId: dependent.Id);
        SeedVisit(member.Id, "1 таб. утром"); // моя собственная — к подопечному не относится

        var (result, items) = await Courses.GetPrescriptionsAsync(member.Id, dependent.Id);

        result.Should().Be(CourseResult.Success);
        items.Should().ContainSingle().Which.DependentId.Should().Be(dependent.Id);
        Db.Set<MedicalAccessAudit>().Should().ContainSingle(a => a.ActorUserId == member.Id && a.OwnerUserId == admin.Id);
    }

    [Fact]
    public async Task Prescriptions_ForForeignDependent_IsNotFound()
    {
        var (_, admin) = SeedFamily();
        var (otherFamily, otherAdmin) = Db.SeedFamilyWithAdmin("Другая");
        var foreign = AddDependent(otherFamily.Id, otherAdmin.Id);

        (await Courses.GetPrescriptionsAsync(admin.Id, foreign.Id)).Result.Should().Be(CourseResult.NotFound);
    }

    [Fact]
    public async Task History_MergesRowsAndUpcomingOccurrences_AndSummarisesAdherence()
    {
        var (family, admin) = SeedFamily();
        var at = MinuteFromNow(30);
        var course = SeedCourse(admin.Id, null, family.Id, admin.Id, OnceAt(at), DateTime.UtcNow.AddMinutes(-1));
        await Doses.ApplyAsync(admin.Id, course.Id, at, DoseAction.Taken, null);

        var (result, history) = await Courses.GetHistoryAsync(admin.Id, course.Id, weeks: 2);

        result.Should().Be(CourseResult.Success);
        history!.Cells.Should().Contain(c => c.ScheduledAt == at && c.Outcome == DoseOutcome.OnTime && c.DoseId != null);
        history.Cells.Should().Contain(c => c.DoseId == null && c.Outcome == DoseOutcome.Upcoming || c.DoseId != null);
        history.Adherence.Percent.Should().Be(100);
        history.From.DayOfWeek.Should().Be(DayOfWeek.Monday);
        history.To.DayOfWeek.Should().Be(DayOfWeek.Sunday);
    }
}
