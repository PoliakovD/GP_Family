using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.Extraction;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>Ветка medicalrecords: референс из бланка приоритетнее справочника — см. докстринг
/// IndicatorFlagCalculator.</summary>
public class IndicatorFlagCalculatorTests
{
    [Fact]
    public void Calculate_ValueWithinBlankRange_ReturnsNormal()
    {
        var indicator = new ExtractedLabIndicator("Гемоглобин", "145", "г/л", 130, 160, null);
        IndicatorFlagCalculator.Calculate(indicator, kbFallback: null, ageYears: null).Should().Be(IndicatorFlag.Normal);
    }

    [Fact]
    public void Calculate_ValueBelowBlankRange_ReturnsLow()
    {
        var indicator = new ExtractedLabIndicator("Гемоглобин", "118", "г/л", 130, 160, null);
        IndicatorFlagCalculator.Calculate(indicator, kbFallback: null, ageYears: null).Should().Be(IndicatorFlag.Low);
    }

    [Fact]
    public void Calculate_ValueAboveBlankRange_ReturnsHigh()
    {
        var indicator = new ExtractedLabIndicator("Гемоглобин", "175", "г/л", 130, 160, null);
        IndicatorFlagCalculator.Calculate(indicator, kbFallback: null, ageYears: null).Should().Be(IndicatorFlag.High);
    }

    [Fact]
    public void Calculate_RussianDecimalComma_ParsesAsNumber()
    {
        var indicator = new ExtractedLabIndicator("Глюкоза", "5,6", "ммоль/л", 3.3, 5.5, null);
        IndicatorFlagCalculator.Calculate(indicator, kbFallback: null, ageYears: null).Should().Be(IndicatorFlag.High);
    }

    [Fact]
    public void Calculate_QualitativeValueMatchesRefText_ReturnsNormal()
    {
        var indicator = new ExtractedLabIndicator("Белок в моче", "отрицательно", null, null, null, "отрицательно");
        IndicatorFlagCalculator.Calculate(indicator, kbFallback: null, ageYears: null).Should().Be(IndicatorFlag.Normal);
    }

    [Fact]
    public void Calculate_QualitativeValueMismatchesRefText_ReturnsUnknown()
    {
        var indicator = new ExtractedLabIndicator("Белок в моче", "положительно", null, null, null, "отрицательно");
        IndicatorFlagCalculator.Calculate(indicator, kbFallback: null, ageYears: null).Should().Be(IndicatorFlag.Unknown);
    }

    [Fact]
    public void Calculate_NoReferenceAnywhere_ReturnsUnknown()
    {
        var indicator = new ExtractedLabIndicator("Загадочный показатель", "42", null, null, null, null);
        IndicatorFlagCalculator.Calculate(indicator, kbFallback: null, ageYears: null).Should().Be(IndicatorFlag.Unknown);
    }

    [Fact]
    public void Calculate_NoBlankReference_FallsBackToKbRangeWhenAgeMatches()
    {
        var indicator = new ExtractedLabIndicator("Показатель", "10", null, null, null, null);
        var kbRange = new KbReferenceRange(AgeFrom: 18, AgeTo: 65, Low: 5, High: 8, Unit: null);

        IndicatorFlagCalculator.Calculate(indicator, kbFallback: kbRange, ageYears: 30).Should().Be(IndicatorFlag.High);
    }

    [Fact]
    public void Calculate_NoBlankReference_KbRangeIgnoredWhenAgeOutOfBounds()
    {
        var indicator = new ExtractedLabIndicator("Показатель", "10", null, null, null, null);
        var kbRange = new KbReferenceRange(AgeFrom: 18, AgeTo: 65, Low: 5, High: 8, Unit: null);

        IndicatorFlagCalculator.Calculate(indicator, kbFallback: kbRange, ageYears: 10).Should().Be(IndicatorFlag.Unknown);
    }
}
