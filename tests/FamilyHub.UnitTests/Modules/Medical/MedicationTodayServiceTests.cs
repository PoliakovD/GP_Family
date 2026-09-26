using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.MedicationCourses;
using FamilyHub.Modules.Medical.MedicationCourses;
using FamilyHub.TestUtils;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical;

public class MedicationTodayServiceTests : MedicationCourseTestBase
{
    /// <summary>Локальная (UTC) дата момента — «Сегодня» запрашиваем именно за неё, чтобы тест не зависел от часа запуска.</summary>
    private static DateOnly DayOf(DateTime utc) => DateOnly.FromDateTime(utc);

    [Fact]
    public async Task Items_ClassifiedByClock_AndCountersAdd()
    {
        var (family, admin) = SeedFamily();
        var missedAt = MinuteFromNow(-(60 * 5));
        var course = SeedCourse(admin.Id, null, family.Id, admin.Id, OnceAt(missedAt), missedAt.AddDays(-1));

        var today = await Today.GetTodayAsync(admin.Id, DayOf(missedAt), null);

        var item = today.Items.Should().ContainSingle(i => i.CourseId == course.Id).Subject;
        item.Outcome.Should().Be(DoseOutcome.Missed);
        item.CanAct.Should().BeTrue();
        today.Counters.Missed.Should().Be(1);
        today.Counters.Total.Should().Be(1);
        today.Subjects.Should().ContainSingle(s => s.IsSelf);
    }

    [Fact]
    public async Task UpcomingAndTaken_ReflectRows()
    {
        var (family, admin) = SeedFamily();
        var soon = MinuteFromNow(30);
        var course = SeedCourse(admin.Id, null, family.Id, admin.Id, OnceAt(soon), DateTime.UtcNow.AddMinutes(-1));

        var before = await Today.GetTodayAsync(admin.Id, DayOf(soon), null);
        before.Items.Single().Outcome.Should().Be(DoseOutcome.Upcoming);
        before.Items.Single().DoseId.Should().BeNull();
        before.Counters.NextAt.Should().Be(soon);

        await Doses.ApplyAsync(admin.Id, course.Id, soon, DoseAction.Taken, null);

        var after = await Today.GetTodayAsync(admin.Id, DayOf(soon), null);
        after.Items.Single().Outcome.Should().Be(DoseOutcome.OnTime);
        after.Items.Single().DoseId.Should().NotBeNull();
        after.Counters.Taken.Should().Be(1);
        after.Counters.NextAt.Should().BeNull();
    }

    [Fact]
    public async Task DependentAndWatchedAdult_AppearWithCorrectAccess_AndMissedRaisesFamilyAlert()
    {
        var (family, admin) = SeedFamily();
        var mama = AddMemberUtc(family.Id);
        mama.FirstName = "Люба";
        var dependent = AddDependent(family.Id, admin.Id, "Бабушка");
        var missedAt = MinuteFromNow(-(60 * 5));
        var schedule = OnceAt(missedAt);
        SeedCourse(mama.Id, null, family.Id, mama.Id, schedule, missedAt.AddDays(-1), name: "Эналаприл");
        SeedCourse(null, dependent.Id, family.Id, admin.Id, schedule, missedAt.AddDays(-1), name: "Аспирин");
        Db.MedicationWatchers.Add(new MedicationWatcher
        {
            Id = Guid.NewGuid(), SubjectUserId = mama.Id, WatcherUserId = admin.Id, NotifyMissed = true, CreatedAt = DateTime.UtcNow,
        });
        Db.SaveChanges();

        var today = await Today.GetTodayAsync(admin.Id, DayOf(missedAt), null);

        today.Items.Should().HaveCount(2);
        today.Items.Single(i => i.DrugName == "Эналаприл").CanAct.Should().BeFalse(); // взрослый — только наблюдение
        today.Items.Single(i => i.DrugName == "Эналаприл").IsWatching.Should().BeTrue();
        today.Items.Single(i => i.DrugName == "Аспирин").CanAct.Should().BeTrue();
        today.Alerts.Select(a => a.DrugName).Should().BeEquivalentTo("Эналаприл", "Аспирин");
        today.Subjects.Select(s => s.Name).Should().Contain(["Люба", "Бабушка"]);
    }

    [Fact]
    public async Task SubjectFilter_NarrowsItems_ButKeepsAllChips()
    {
        var (family, admin) = SeedFamily();
        var dependent = AddDependent(family.Id, admin.Id);
        var at = MinuteFromNow(30);
        SeedCourse(admin.Id, null, family.Id, admin.Id, OnceAt(at), DateTime.UtcNow.AddMinutes(-1), name: "Мой");
        SeedCourse(null, dependent.Id, family.Id, admin.Id, OnceAt(at), DateTime.UtcNow.AddMinutes(-1), name: "Её");

        var me = await Today.GetTodayAsync(admin.Id, DayOf(at), "me");
        var dep = await Today.GetTodayAsync(admin.Id, DayOf(at), $"d:{dependent.Id}");

        me.Items.Select(i => i.DrugName).Should().Equal("Мой");
        dep.Items.Select(i => i.DrugName).Should().Equal("Её");
        me.Subjects.Should().HaveCount(2);
    }

    [Fact]
    public async Task Stranger_SeesNothing()
    {
        var (family, admin) = SeedFamily();
        var at = MinuteFromNow(30);
        SeedCourse(admin.Id, null, family.Id, admin.Id, OnceAt(at), DateTime.UtcNow.AddMinutes(-1));
        var stranger = Db.AddUser();

        var today = await Today.GetTodayAsync(stranger.Id, DayOf(at), null);

        today.Items.Should().BeEmpty();
        today.Subjects.Should().BeEmpty();
    }

    [Fact]
    public async Task PausedCourse_IsNotShown()
    {
        var (family, admin) = SeedFamily();
        var at = MinuteFromNow(30);
        SeedCourse(admin.Id, null, family.Id, admin.Id, OnceAt(at), DateTime.UtcNow.AddMinutes(-1), MedicationCourseStatus.Paused);

        (await Today.GetTodayAsync(admin.Id, DayOf(at), null)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task AsNeeded_ShowsTakenTodayAgainstLimit()
    {
        var (family, admin) = SeedFamily();
        var course = SeedCourse(admin.Id, null, family.Id, admin.Id, AsNeeded(3), DateTime.UtcNow.AddMinutes(-1), name: "Нурофен");
        await Doses.TakeAsNeededAsync(admin.Id, course.Id, new PrnRequest(null, false));

        var today = await Today.GetTodayAsync(admin.Id, DayOf(DateTime.UtcNow), null);

        var prn = today.AsNeeded.Should().ContainSingle().Subject;
        prn.MaxPerDay.Should().Be(3);
        prn.TakenToday.Should().Be(1);
    }

    [Fact]
    public async Task LowStock_ListedWhenDaysCoveredWithinThreshold()
    {
        var (family, admin) = SeedFamily();
        var med = AddMedication(family.Id, admin.Id, "6 таб.");
        var twice = new DoseSchedule(DoseScheduleMode.TimesPerDay,
            Times: [new DoseTime(new TimeOnly(8, 0), 1), new DoseTime(new TimeOnly(20, 0), 1)]);
        SeedCourse(admin.Id, null, family.Id, admin.Id, twice, medicationId: med.Id, writeOff: true);

        var today = await Today.GetTodayAsync(admin.Id, null, null);

        var low = today.LowStock.Should().ContainSingle().Subject;
        low.DaysCovered.Should().Be(3); // 6 таблеток при 2 в день — как в макете
        low.QuantityText.Should().Be("6 таб.");
    }

    [Fact]
    public async Task Week_CoversMondayToSunday_WithTodayMarked()
    {
        var (family, admin) = SeedFamily();
        SeedCourse(admin.Id, null, family.Id, admin.Id, OnceAt(MinuteFromNow(30)), DateTime.UtcNow.AddMinutes(-1));

        var today = await Today.GetTodayAsync(admin.Id, null, null);

        today.Week.Should().HaveCount(7);
        today.Week.First().Date.DayOfWeek.Should().Be(DayOfWeek.Monday);
        today.Week.Should().ContainSingle(d => d.IsToday);
    }

    [Fact]
    public async Task AttentionCount_CountsDueAndMissed_OnlyWhereICanAct()
    {
        var (family, admin) = SeedFamily();
        var mama = AddMemberUtc(family.Id);
        var missedAt = MinuteFromNow(-(60 * 5));
        // Свой пропущенный приём (сегодня по UTC) — считается; чужой взрослого, за которым лишь слежу, — нет.
        SeedCourse(admin.Id, null, family.Id, admin.Id, OnceAt(missedAt), missedAt.AddDays(-1));
        SeedCourse(mama.Id, null, family.Id, mama.Id, OnceAt(missedAt), missedAt.AddDays(-1));
        Db.MedicationWatchers.Add(new MedicationWatcher
        {
            Id = Guid.NewGuid(), SubjectUserId = mama.Id, WatcherUserId = admin.Id, NotifyMissed = true, CreatedAt = DateTime.UtcNow,
        });
        Db.SaveChanges();

        var count = await Today.GetAttentionCountAsync(admin.Id);

        // Если тест идёт в первые 5 часов суток, «5 часов назад» — уже вчера и не попадает в «сегодня».
        count.Should().Be(DayOf(missedAt) == DayOf(DateTime.UtcNow) ? 1 : 0);
    }
}
