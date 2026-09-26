using FamilyHub.Domain.Enums;
using FamilyHub.Domain.MedicationCourses;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.MedicationCourses;

public class MedicationCourseRulesTests
{
    private static readonly DateOnly Start = new(2026, 9, 26);

    private static MedicationCourseContent Course(DoseSchedule? s = null, string? name = "Сорбифер", DateOnly? end = null) =>
        new(name, s ?? Twice(), Start, end);

    private static DoseSchedule Twice() => new(DoseScheduleMode.TimesPerDay,
        Times: [new DoseTime(new TimeOnly(8, 0), 1), new DoseTime(new TimeOnly(20, 0), 1)]);

    [Fact]
    public void Valid_Passes() => MedicationCourseRules.Validate(Course(end: Start.AddDays(56))).Should().BeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void DrugName_Required(string? name) => MedicationCourseRules.Validate(Course(name: name)).Should().NotBeNull();

    [Fact]
    public void EndBeforeStart_Rejected() => MedicationCourseRules.Validate(Course(end: Start.AddDays(-1))).Should().NotBeNull();

    [Fact]
    public void DuplicateTimes_Rejected() =>
        MedicationCourseRules.ValidateSchedule(new DoseSchedule(DoseScheduleMode.TimesPerDay,
            Times: [new DoseTime(new TimeOnly(8, 0), 1), new DoseTime(new TimeOnly(8, 0), 1)])).Should().NotBeNull();

    [Fact]
    public void TooManyTimes_Rejected() =>
        MedicationCourseRules.ValidateSchedule(new DoseSchedule(DoseScheduleMode.TimesPerDay,
            Times: Enumerable.Range(0, 13).Select(i => new DoseTime(new TimeOnly(i, 0), 1)).ToList())).Should().NotBeNull();

    [Theory]
    [InlineData(0.1)]
    [InlineData(101)]
    public void UnitsOutOfRange_Rejected(double units) =>
        MedicationCourseRules.ValidateSchedule(new DoseSchedule(DoseScheduleMode.TimesPerDay,
            Times: [new DoseTime(new TimeOnly(8, 0), (decimal)units)])).Should().NotBeNull();

    [Theory]
    [InlineData(5, false)]
    [InlineData(7, false)]
    [InlineData(12, true)]
    [InlineData(6, true)]
    public void EveryNHours_OnlyDivisorsOfDay(int hours, bool ok) =>
        (MedicationCourseRules.ValidateSchedule(new DoseSchedule(DoseScheduleMode.EveryNHours, IntervalHours: hours,
            IntervalStart: new TimeOnly(8, 0), IntervalUnits: 1)) is null).Should().Be(ok);

    [Fact]
    public void Weekdays_NeedAtLeastOneDay_NoDuplicates()
    {
        var times = new[] { new DoseTime(new TimeOnly(9, 0), 1) };
        MedicationCourseRules.ValidateSchedule(new DoseSchedule(DoseScheduleMode.Weekdays, Times: times, Weekdays: [])).Should().NotBeNull();
        MedicationCourseRules.ValidateSchedule(new DoseSchedule(DoseScheduleMode.Weekdays, Times: times,
            Weekdays: [DayOfWeek.Monday, DayOfWeek.Monday])).Should().NotBeNull();
        MedicationCourseRules.ValidateSchedule(new DoseSchedule(DoseScheduleMode.Weekdays, Times: times,
            Weekdays: [DayOfWeek.Monday, DayOfWeek.Friday])).Should().BeNull();
    }

    [Theory]
    [InlineData(21, 7, true)]
    [InlineData(0, 7, false)]
    [InlineData(21, 0, false)]
    [InlineData(91, 7, false)]
    public void Cycle_BoundsChecked(int on, int off, bool ok) =>
        (MedicationCourseRules.ValidateSchedule(new DoseSchedule(DoseScheduleMode.Cycle,
            Times: [new DoseTime(new TimeOnly(9, 0), 1)], CycleOnDays: on, CycleOffDays: off)) is null).Should().Be(ok);

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(3, true)]
    [InlineData(25, false)]
    public void AsNeeded_RequiresDailyLimit(int? max, bool ok) =>
        (MedicationCourseRules.ValidateSchedule(new DoseSchedule(DoseScheduleMode.AsNeeded, MaxPerDay: max)) is null).Should().Be(ok);

    [Fact]
    public void ReminderSettings_Validated()
    {
        MedicationCourseRules.Validate(Course() with { RepeatAfterMinutes = 15 }).Should().BeNull();
        MedicationCourseRules.Validate(Course() with { RepeatAfterMinutes = 7 }).Should().NotBeNull();
        MedicationCourseRules.Validate(Course() with { MissedAfterMinutes = 45 }).Should().NotBeNull();
        MedicationCourseRules.Validate(Course() with { LowStockDays = 0 }).Should().NotBeNull();
    }

    [Fact]
    public void Schedule_RoundTripsThroughJson()
    {
        var s = new DoseSchedule(DoseScheduleMode.Weekdays, Times: [new DoseTime(new TimeOnly(9, 30), 1.5m)],
            Weekdays: [DayOfWeek.Monday, DayOfWeek.Friday]);

        var back = MedicationCourseRules.ParseSchedule(MedicationCourseRules.SerializeSchedule(s));

        back.Should().NotBeNull();
        back!.Mode.Should().Be(DoseScheduleMode.Weekdays);
        back.Times.Should().Equal(s.Times);
        back.Weekdays.Should().Equal(s.Weekdays);
    }

    [Fact]
    public void ParseSchedule_BrokenJson_ReturnsNull()
    {
        MedicationCourseRules.ParseSchedule("{not json").Should().BeNull();
        MedicationCourseRules.ParseSchedule(null).Should().BeNull();
    }
}
