using FamilyHub.Domain.Enums;
using FamilyHub.Domain.MedicationCourses;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.MedicationCourses;

public class DoseScheduleExpanderTests
{
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    private static DoseSchedule TwiceDaily() => new(DoseScheduleMode.TimesPerDay,
        Times: [new DoseTime(new TimeOnly(20, 0), 1), new DoseTime(new TimeOnly(8, 0), 1)]);

    [Fact]
    public void TimesPerDay_ReturnsLocalTimesConvertedToUtc_Sorted()
    {
        // Москва — UTC+3 без перевода часов: локальные сутки 26.09 = [25.09 21:00Z, 26.09 21:00Z).
        var result = DoseScheduleExpander.Expand(TwiceDaily(), new DateOnly(2026, 9, 1), null, Moscow,
            Utc(2026, 9, 25, 21), Utc(2026, 9, 26, 21));

        result.Select(o => o.ScheduledAtUtc).Should().Equal(Utc(2026, 9, 26, 5), Utc(2026, 9, 26, 17));
        result.Select(o => o.LocalTime).Should().Equal(new TimeOnly(8, 0), new TimeOnly(20, 0));
        result.Should().OnlyContain(o => o.LocalDate == new DateOnly(2026, 9, 26));
    }

    [Fact]
    public void EveryNHours_12FromEight_GivesEightAndTwenty()
    {
        var s = new DoseSchedule(DoseScheduleMode.EveryNHours, IntervalHours: 12, IntervalStart: new TimeOnly(8, 0), IntervalUnits: 1);

        var result = DoseScheduleExpander.Expand(s, new DateOnly(2026, 9, 1), null, Moscow, Utc(2026, 9, 25, 21), Utc(2026, 9, 26, 21));

        result.Select(o => o.LocalTime).Should().Equal(new TimeOnly(8, 0), new TimeOnly(20, 0));
    }

    [Fact]
    public void EveryNHours_8FromLateEvening_WrapsPastMidnightWithinSameDayList()
    {
        var s = new DoseSchedule(DoseScheduleMode.EveryNHours, IntervalHours: 8, IntervalStart: new TimeOnly(22, 0), IntervalUnits: 1);

        s.DailyTimes().Select(t => t.At).Should().Equal(new TimeOnly(6, 0), new TimeOnly(14, 0), new TimeOnly(22, 0));
    }

    [Fact]
    public void Weekdays_OnlySelectedDays()
    {
        var s = new DoseSchedule(DoseScheduleMode.Weekdays, Times: [new DoseTime(new TimeOnly(9, 0), 1)],
            Weekdays: [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday]);

        // 21.09.2026 — понедельник. Окно — локальная неделя 21..27.
        var result = DoseScheduleExpander.Expand(s, new DateOnly(2026, 9, 1), null, Moscow, Utc(2026, 9, 20, 21), Utc(2026, 9, 27, 21));

        result.Select(o => o.LocalDate).Should().Equal(new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 23), new DateOnly(2026, 9, 25));
    }

    [Fact]
    public void Cycle_21On7Off_SkipsBreakAndResumes()
    {
        var s = new DoseSchedule(DoseScheduleMode.Cycle, Times: [new DoseTime(new TimeOnly(9, 0), 1)], CycleOnDays: 21, CycleOffDays: 7);
        var start = new DateOnly(2026, 9, 1);

        // День 20 (21.09) — последний приёмный, дни 21..27 (22..28.09) — перерыв, 29.09 — снова приём.
        var result = DoseScheduleExpander.Expand(s, start, null, Moscow, Utc(2026, 9, 20, 21), Utc(2026, 9, 29, 21));

        result.Select(o => o.LocalDate).Should().Equal(new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 29));
        DoseScheduleExpander.NextBreakStart(s, start, null, start).Should().Be(new DateOnly(2026, 9, 22));
        // Внутри перерыва «ближайший» перерыв — следующий, через полный цикл.
        DoseScheduleExpander.NextBreakStart(s, start, null, new DateOnly(2026, 9, 25)).Should().Be(new DateOnly(2026, 10, 20));
    }

    [Fact]
    public void Cycle_EveryOtherDay_Alternates()
    {
        var s = new DoseSchedule(DoseScheduleMode.Cycle, Times: [new DoseTime(new TimeOnly(9, 0), 1)], CycleOnDays: 1, CycleOffDays: 1);

        var result = DoseScheduleExpander.Expand(s, new DateOnly(2026, 9, 1), null, Moscow, Utc(2026, 8, 31, 21), Utc(2026, 9, 6, 21));

        result.Select(o => o.LocalDate.Day).Should().Equal(1, 3, 5);
    }

    [Fact]
    public void AsNeeded_ProducesNothing()
    {
        var s = new DoseSchedule(DoseScheduleMode.AsNeeded, MaxPerDay: 3);

        DoseScheduleExpander.Expand(s, new DateOnly(2026, 9, 1), null, Moscow, Utc(2026, 9, 1), Utc(2026, 10, 1)).Should().BeEmpty();
    }

    [Fact]
    public void CourseBounds_AreInclusive()
    {
        var s = new DoseSchedule(DoseScheduleMode.TimesPerDay, Times: [new DoseTime(new TimeOnly(9, 0), 1)]);

        var result = DoseScheduleExpander.Expand(s, new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 12), Moscow,
            Utc(2026, 9, 1), Utc(2026, 10, 1));

        result.Select(o => o.LocalDate.Day).Should().Equal(10, 11, 12);
    }

    [Fact]
    public void Window_IsFromInclusiveToExclusive()
    {
        var s = new DoseSchedule(DoseScheduleMode.TimesPerDay, Times: [new DoseTime(new TimeOnly(9, 0), 1)]);
        var at = Utc(2026, 9, 26, 6); // 9:00 МСК

        DoseScheduleExpander.Expand(s, new DateOnly(2026, 9, 1), null, Moscow, at, at.AddMinutes(1)).Should().HaveCount(1);
        DoseScheduleExpander.Expand(s, new DateOnly(2026, 9, 1), null, Moscow, at.AddMinutes(1), at.AddHours(1)).Should().BeEmpty();
        DoseScheduleExpander.Expand(s, new DateOnly(2026, 9, 1), null, Moscow, at.AddHours(-1), at).Should().BeEmpty();
    }

    [Fact]
    public void ToUtc_TimeInSpringGap_ShiftsForwardByGapSize()
    {
        // Берлин 29.03.2026: в 02:00 часы переводятся на 03:00 — 02:30 не существует → 03:30 CEST = 01:30Z.
        DoseScheduleExpander.ToUtc(new DateOnly(2026, 3, 29), new TimeOnly(2, 30), Berlin).Should().Be(Utc(2026, 3, 29, 1, 30));
    }

    [Fact]
    public void ToUtc_AmbiguousAutumnTime_TakesFirstOccurrence()
    {
        // Берлин 25.10.2026: 02:30 бывает дважды; первое — ещё CEST (UTC+2) → 00:30Z.
        DoseScheduleExpander.ToUtc(new DateOnly(2026, 10, 25), new TimeOnly(2, 30), Berlin).Should().Be(Utc(2026, 10, 25, 0, 30));
    }

    [Fact]
    public void Expand_AcrossDstChange_KeepsLocalWallClock()
    {
        var s = new DoseSchedule(DoseScheduleMode.TimesPerDay, Times: [new DoseTime(new TimeOnly(8, 0), 1)]);

        var result = DoseScheduleExpander.Expand(s, new DateOnly(2026, 3, 1), null, Berlin, Utc(2026, 3, 28), Utc(2026, 3, 31));

        // До перехода 8:00 CET = 07:00Z, после — 8:00 CEST = 06:00Z.
        result.Select(o => o.ScheduledAtUtc).Should().Equal(Utc(2026, 3, 28, 7), Utc(2026, 3, 29, 6), Utc(2026, 3, 30, 6));
    }

    [Fact]
    public void CountUnits_SumsActiveDaysWithinCourse()
    {
        // 26.09..07.10 = 12 дней × 2 приёма.
        DoseScheduleExpander.CountUnits(TwiceDaily(), new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 7),
            new DateOnly(2026, 9, 26), new DateOnly(2026, 12, 31)).Should().Be(24);
    }

    [Fact]
    public void AverageUnitsPerDay_AccountsForWeekdaysAndCycle()
    {
        var oncePerDay = new[] { new DoseTime(new TimeOnly(9, 0), 2) };

        DoseScheduleExpander.AverageUnitsPerDay(new DoseSchedule(DoseScheduleMode.TimesPerDay, Times: oncePerDay)).Should().Be(2);
        DoseScheduleExpander.AverageUnitsPerDay(new DoseSchedule(DoseScheduleMode.Weekdays, Times: oncePerDay,
            Weekdays: [DayOfWeek.Monday, DayOfWeek.Friday])).Should().BeApproximately(4m / 7, 0.0001m);
        DoseScheduleExpander.AverageUnitsPerDay(new DoseSchedule(DoseScheduleMode.Cycle, Times: oncePerDay,
            CycleOnDays: 21, CycleOffDays: 7)).Should().Be(1.5m);
        DoseScheduleExpander.AverageUnitsPerDay(new DoseSchedule(DoseScheduleMode.AsNeeded, MaxPerDay: 3)).Should().Be(0);
    }
}
