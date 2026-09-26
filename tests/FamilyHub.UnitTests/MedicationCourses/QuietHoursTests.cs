using FamilyHub.Domain.MedicationCourses;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.MedicationCourses;

public class QuietHoursTests
{
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
    private static readonly TimeOnly From = new(23, 0);
    private static readonly TimeOnly To = new(7, 0);

    private static DateTime AtMoscow(int hour, int minute = 0) =>
        new DateTime(2026, 9, 26, hour, minute, 0, DateTimeKind.Utc).AddHours(-3);

    [Theory]
    [InlineData(23, 0, true)]
    [InlineData(2, 30, true)]
    [InlineData(6, 59, true)]
    [InlineData(7, 0, false)]
    [InlineData(14, 0, false)]
    [InlineData(22, 59, false)]
    public void WrapsPastMidnight(int hour, int minute, bool quiet) =>
        QuietHours.IsQuiet(From, To, AtMoscow(hour, minute), Moscow).Should().Be(quiet);

    [Fact]
    public void SameDayInterval()
    {
        QuietHours.IsQuiet(new TimeOnly(13, 0), new TimeOnly(15, 0), AtMoscow(14), Moscow).Should().BeTrue();
        QuietHours.IsQuiet(new TimeOnly(13, 0), new TimeOnly(15, 0), AtMoscow(15), Moscow).Should().BeFalse();
    }

    [Fact]
    public void DisabledWhenBoundaryMissingOrEqual()
    {
        QuietHours.IsQuiet(null, To, AtMoscow(2), Moscow).Should().BeFalse();
        QuietHours.IsQuiet(From, null, AtMoscow(2), Moscow).Should().BeFalse();
        QuietHours.IsQuiet(From, From, AtMoscow(2), Moscow).Should().BeFalse();
    }
}
