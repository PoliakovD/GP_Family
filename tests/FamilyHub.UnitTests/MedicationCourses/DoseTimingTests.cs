using FamilyHub.Domain.Enums;
using FamilyHub.Domain.MedicationCourses;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.MedicationCourses;

public class DoseTimingTests
{
    private static readonly DateTime T = new(2026, 9, 26, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MissedDeadline_IsScheduledPlusThreshold() =>
        DoseTiming.MissedDeadline(T, 120).Should().Be(T.AddHours(2));

    [Fact]
    public void MissedDeadline_AfterSnooze_ExtendsByGrace()
    {
        // Отложили до 10:50 → дедлайн 11:20, позже, чем 9:00 + 2 ч.
        DoseTiming.MissedDeadline(T, 120, T.AddMinutes(110)).Should().Be(T.AddMinutes(140));
        // Короткая отсрочка дедлайн не сдвигает.
        DoseTiming.MissedDeadline(T, 120, T.AddMinutes(10)).Should().Be(T.AddHours(2));
    }

    [Theory]
    [InlineData(5, DoseOutcome.OnTime)]
    [InlineData(60, DoseOutcome.OnTime)]
    [InlineData(61, DoseOutcome.Late)]
    public void Taken_LateAfterAnHour(int minutesAfter, DoseOutcome expected) =>
        DoseTiming.Classify(DoseStatus.Taken, T, T.AddMinutes(minutesAfter), 120, null, T.AddDays(1)).Should().Be(expected);

    [Fact]
    public void Skipped_IsSkipped_NotMissed() =>
        DoseTiming.Classify(DoseStatus.Skipped, T, null, 120, null, T.AddDays(1)).Should().Be(DoseOutcome.Skipped);

    [Theory]
    [InlineData(-30, DoseOutcome.Upcoming)]
    [InlineData(0, DoseOutcome.Due)]
    [InlineData(119, DoseOutcome.Due)]
    [InlineData(120, DoseOutcome.Missed)]
    public void NoRowOrPending_ClassifiedByClock(int minutesFromScheduled, DoseOutcome expected)
    {
        var now = T.AddMinutes(minutesFromScheduled);

        DoseTiming.Classify(null, T, null, 120, null, now).Should().Be(expected);
        DoseTiming.Classify(DoseStatus.Pending, T, null, 120, null, now).Should().Be(expected);
    }

    [Fact]
    public void OnTimePercent_IgnoresSkippedAndFuture()
    {
        DoseTiming.OnTimePercent([DoseOutcome.OnTime, DoseOutcome.OnTime, DoseOutcome.Late, DoseOutcome.Missed,
            DoseOutcome.Skipped, DoseOutcome.Upcoming, DoseOutcome.Due]).Should().Be(50);
        DoseTiming.OnTimePercent([DoseOutcome.Upcoming, DoseOutcome.Skipped]).Should().BeNull();
    }
}
