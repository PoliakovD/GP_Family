using FamilyHub.Modules.Medical.Extraction;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>Живой баг: "&lt;47" должно означать диапазон 0-47, "&gt;47" — "от 47 без верхней
/// границы" — не оставаться нераспознанным текстом, из-за которого показатель навсегда застревает
/// на IndicatorFlag.Unknown (см. IndicatorFlagCalculator.Calculate).</summary>
public class ReferenceRangeTextParserTests
{
    [Theory]
    [InlineData("<47", 0, 47)]
    [InlineData("≤47", 0, 47)]
    [InlineData("до 47", 0, 47)]
    [InlineData("менее 47", 0, 47)]
    [InlineData("не более 47", 0, 47)]
    [InlineData("47 и менее", 0, 47)]
    [InlineData("<0,5", 0, 0.5)]
    public void TryParse_UpperBoundOnly_ReturnsZeroToBound(string text, double expectedLow, double expectedHigh)
    {
        var result = ReferenceRangeTextParser.TryParse(text);
        result.Should().Be(((double?)expectedLow, (double?)expectedHigh));
    }

    [Theory]
    [InlineData(">47", 47)]
    [InlineData("≥47", 47)]
    [InlineData("от 47", 47)]
    [InlineData("более 47", 47)]
    [InlineData("не менее 47", 47)]
    [InlineData("47 и более", 47)]
    public void TryParse_LowerBoundOnly_ReturnsBoundToNull(string text, double expectedLow)
    {
        var result = ReferenceRangeTextParser.TryParse(text);
        result.Should().Be(((double?)expectedLow, (double?)null));
    }

    [Theory]
    [InlineData("130-160", 130, 160)]
    [InlineData("130 – 160", 130, 160)]
    [InlineData("130..160", 130, 160)]
    [InlineData("3,3-5,5", 3.3, 5.5)]
    public void TryParse_TwoSidedRange_ReturnsBothBounds(string text, double expectedLow, double expectedHigh)
    {
        var result = ReferenceRangeTextParser.TryParse(text);
        result.Should().Be(((double?)expectedLow, (double?)expectedHigh));
    }

    [Theory]
    [InlineData("отрицательно")]
    [InlineData("1-3 в п/зр")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_NotARange_ReturnsNull(string? text)
    {
        ReferenceRangeTextParser.TryParse(text).Should().BeNull();
    }

    [Theory]
    [InlineData("<0,5", 0.5)]
    [InlineData(">1000", 1000)]
    public void ParseCensoredValue_PrefixedNumber_ReturnsNumericPart(string value, double expected)
    {
        ReferenceRangeTextParser.ParseCensoredValue(value).Should().Be(expected);
    }

    [Fact]
    public void ParseCensoredValue_PlainNumber_ReturnsNull()
    {
        // Обычное число парсится IndicatorFlagCalculator.ParseNumeric, не этим методом.
        ReferenceRangeTextParser.ParseCensoredValue("47").Should().BeNull();
    }
}
