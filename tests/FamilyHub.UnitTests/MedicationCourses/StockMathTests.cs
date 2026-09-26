using FamilyHub.Domain.MedicationCourses;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.MedicationCourses;

public class StockMathTests
{
    [Theory]
    [InlineData("22", 22)]
    [InlineData("22 таб.", 22)]
    [InlineData("  1,5 упаковки", 1.5)]
    [InlineData("0.25", 0.25)]
    public void TryParse_ReadsLeadingNumber(string text, double expected)
    {
        StockMath.TryParse(text, out var q).Should().BeTrue();
        q.Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("много")]
    [InlineData("около 20")]
    public void TryParse_NonNumeric_ReturnsFalse(string? text) => StockMath.TryParse(text, out _).Should().BeFalse();

    [Theory]
    [InlineData("22 таб.", 21, "21 таб.")]
    [InlineData("1,5 упаковки", 0.5, "0,5 упаковки")]
    [InlineData("10", 9.25, "9.25")]
    [InlineData("3 таб.", -1, "0 таб.")]
    public void ReplaceQuantity_KeepsSuffixAndSeparator(string original, double newQty, string expected) =>
        StockMath.ReplaceQuantity(original, (decimal)newQty).Should().Be(expected);

    [Fact]
    public void ReplaceQuantity_Unparseable_ReturnsNull() => StockMath.ReplaceQuantity("много", 1).Should().BeNull();

    [Fact]
    public void DaysCovered_FloorsAndHandlesZeroUsage()
    {
        StockMath.DaysCovered(22, 2).Should().Be(11);
        StockMath.DaysCovered(6, 2).Should().Be(3);
        StockMath.DaysCovered(5, 2).Should().Be(2);
        StockMath.DaysCovered(5, 0).Should().BeNull();
    }

    [Fact]
    public void Shortfall_RoundsUp()
    {
        StockMath.Shortfall(112, 22).Should().Be(90);
        StockMath.Shortfall(10.5m, 10).Should().Be(1);
        StockMath.Shortfall(10, 22).Should().Be(0);
    }
}
