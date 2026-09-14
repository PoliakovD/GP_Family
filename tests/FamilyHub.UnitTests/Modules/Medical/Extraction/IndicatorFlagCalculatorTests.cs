using System.Globalization;
using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.Extraction;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>Ветка medicalrecords (редизайн v2): референс из бланка приоритетнее KB, KB приоритетнее
/// расчёта — см. докстринг IndicatorFlagCalculator (каскад RefSource).</summary>
public class IndicatorFlagCalculatorTests
{
    [Fact]
    public void Calculate_ValueWithinBlankRange_ReturnsNormal()
    {
        var indicator = new ExtractedLabIndicator("Гемоглобин", "145", "г/л", 130, 160, null);
        var (flag, source, _, _) = IndicatorFlagCalculator.Calculate(indicator, kbFallback: null, ageYears: null, sex: null);
        flag.Should().Be(IndicatorFlag.Normal);
        source.Should().Be(RefSource.Blank);
    }

    [Fact]
    public void Calculate_ValueBelowBlankRange_ReturnsLow()
    {
        var indicator = new ExtractedLabIndicator("Гемоглобин", "118", "г/л", 130, 160, null);
        IndicatorFlagCalculator.Calculate(indicator, kbFallback: null, ageYears: null, sex: null).Flag.Should().Be(IndicatorFlag.Low);
    }

    [Fact]
    public void Calculate_ValueAboveBlankRange_ReturnsHigh()
    {
        var indicator = new ExtractedLabIndicator("Гемоглобин", "175", "г/л", 130, 160, null);
        IndicatorFlagCalculator.Calculate(indicator, kbFallback: null, ageYears: null, sex: null).Flag.Should().Be(IndicatorFlag.High);
    }

    [Fact]
    public void Calculate_RussianDecimalComma_ParsesAsNumber()
    {
        var indicator = new ExtractedLabIndicator("Глюкоза", "5,6", "ммоль/л", 3.3, 5.5, null);
        IndicatorFlagCalculator.Calculate(indicator, kbFallback: null, ageYears: null, sex: null).Flag.Should().Be(IndicatorFlag.High);
    }

    [Fact]
    public void Calculate_QualitativeValueMatchesRefText_ReturnsNormal()
    {
        var indicator = new ExtractedLabIndicator("Белок в моче", "отрицательно", null, null, null, "отрицательно");
        IndicatorFlagCalculator.Calculate(indicator, kbFallback: null, ageYears: null, sex: null).Flag.Should().Be(IndicatorFlag.Normal);
    }

    /// <summary>Раньше это было ...ReturnsUnknown — явное РАСХОЖДЕНИЕ смысла ("положительно" при
    /// напечатанной норме "отрицательно") молчало серым "?" из-за голого сравнения строк.
    /// QualitativeResultClassifier различает полярность — расхождение теперь однозначный High, не
    /// "нет данных" (см. IndicatorFlagCalculator.CompareQualitative).</summary>
    [Fact]
    public void Calculate_QualitativeValueMismatchesRefText_ReturnsHigh()
    {
        var indicator = new ExtractedLabIndicator("Белок в моче", "положительно", null, null, null, "отрицательно");
        IndicatorFlagCalculator.Calculate(indicator, kbFallback: null, ageYears: null, sex: null).Flag.Should().Be(IndicatorFlag.High);
    }

    [Fact]
    public void Calculate_QualitativeSynonymMatchesRefText_ReturnsNormal()
    {
        // "не обнаружены" (мн.ч.) против нормы "не обнаружено" (ед.ч.) — раньше не совпадало ни
        // числом, ни родом (голое равенство строк), молчало Unknown. Тот же смысл — теперь Normal.
        var indicator = new ExtractedLabIndicator("Chlamydia trachomatis", "не обнаружены", null, null, null, "не обнаружено");
        IndicatorFlagCalculator.Calculate(indicator, kbFallback: null, ageYears: null, sex: null).Flag.Should().Be(IndicatorFlag.Normal);
    }

    [Theory]
    [InlineData("<47", "12", IndicatorFlag.Normal, 0, 47.0)]
    [InlineData("<47", "60", IndicatorFlag.High, 0, 47.0)]
    [InlineData(">47", "50", IndicatorFlag.Normal, 47, null)]
    [InlineData(">47", "30", IndicatorFlag.Low, 47, null)]
    public void Calculate_OneSidedTextualRefText_ParsesIntoOneSidedBounds(
        string refText, string value, IndicatorFlag expectedFlag, double expectedLow, double? expectedHigh)
    {
        var indicator = new ExtractedLabIndicator("Показатель", value, null, null, null, refText);
        var (flag, source, effLow, effHigh) = IndicatorFlagCalculator.Calculate(indicator, kbFallback: null, ageYears: null, sex: null);

        flag.Should().Be(expectedFlag);
        source.Should().Be(RefSource.Blank);
        effLow.Should().Be(expectedLow);
        effHigh.Should().Be(expectedHigh);
    }

    [Fact]
    public void Calculate_CensoredValueBelowUpperBound_ReturnsNormal()
    {
        // "<0,5" — за пределами чувствительности метода, а не "нет данных": числовая часть
        // сравнивается с диапазоном как обычное значение (см. ReferenceRangeTextParser.ParseCensoredValue).
        var indicator = new ExtractedLabIndicator("Показатель", "<0,5", null, 0, 1, null);
        IndicatorFlagCalculator.Calculate(indicator, kbFallback: null, ageYears: null, sex: null).Flag.Should().Be(IndicatorFlag.Normal);
    }

    [Fact]
    public void Calculate_NegativeFindingValue_NoReferenceAtAll_ReturnsNormalWithoutKbFallback()
    {
        // Типичный бланк ИППП: колонки референса нет вовсе (не число, не текст), но "не
        // обнаружено" само по себе — осмысленная норма для такого теста, а не "нет данных".
        var indicator = new ExtractedLabIndicator("Chlamydia trachomatis", "не обнаружено", null, null, null, null);
        var (flag, source, _, _) = IndicatorFlagCalculator.Calculate(indicator, kbFallback: null, ageYears: null, sex: null);
        flag.Should().Be(IndicatorFlag.Normal);
        source.Should().Be(RefSource.Blank);
    }

    [Fact]
    public void Calculate_NonNumericValue_DoesNotConsumeNumericKbRange_FallsThroughToNone()
    {
        // Раньше нечисловое значение против числового KB-диапазона давало (Unknown, KbFixed) и
        // НАВСЕГДА блокировало дальнейший пересчёт (KbFixed не уступает место ни KbCalculated, ни
        // Inferred) — должно проваливаться дальше по каскаду.
        var indicator = new ExtractedLabIndicator("Показатель", "норма", null, null, null, null);
        var kbRange = new KbReferenceRange(AgeFrom: null, AgeTo: null, Sex: null, Low: 5, High: 8, Unit: null);

        var (flag, source, _, _) = IndicatorFlagCalculator.Calculate(indicator, kbFallback: kbRange, ageYears: null, sex: null);
        flag.Should().Be(IndicatorFlag.Unknown);
        source.Should().Be(RefSource.None);
    }

    [Fact]
    public void TryApplyInferred_NumericRefExpected_ReturnsInferredSource()
    {
        var indicator = new ExtractedLabIndicator("Показатель", "6", null, null, null, null, RefExpected: "3,5-5,0");
        var result = IndicatorFlagCalculator.TryApplyInferred(indicator);

        result.Should().NotBeNull();
        result!.Value.Flag.Should().Be(IndicatorFlag.High);
        result.Value.Source.Should().Be(RefSource.Inferred);
        result.Value.Low.Should().Be(3.5);
        result.Value.High.Should().Be(5.0);
    }

    [Fact]
    public void TryApplyInferred_QualitativeRefExpected_MatchingValue_ReturnsNormal()
    {
        // Бланк ИППП без референса вовсе — модель сама предположила ожидаемую норму.
        var indicator = new ExtractedLabIndicator("Chlamydia trachomatis", "не обнаружено", null, null, null, null, RefExpected: "не обнаружено");
        var result = IndicatorFlagCalculator.TryApplyInferred(indicator);

        result.Should().NotBeNull();
        result!.Value.Flag.Should().Be(IndicatorFlag.Normal);
        result.Value.Source.Should().Be(RefSource.Inferred);
    }

    [Fact]
    public void TryApplyInferred_NoRefExpected_ReturnsNull()
    {
        var indicator = new ExtractedLabIndicator("Показатель", "10", null, null, null, null);
        IndicatorFlagCalculator.TryApplyInferred(indicator).Should().BeNull();
    }

    [Fact]
    public void Calculate_NoReferenceAnywhere_ReturnsUnknown()
    {
        var indicator = new ExtractedLabIndicator("Загадочный показатель", "42", null, null, null, null);
        var (flag, source, _, _) = IndicatorFlagCalculator.Calculate(indicator, kbFallback: null, ageYears: null, sex: null);
        flag.Should().Be(IndicatorFlag.Unknown);
        source.Should().Be(RefSource.None);
    }

    [Fact]
    public void Calculate_NoBlankReference_FallsBackToKbRangeWhenAgeMatches()
    {
        var indicator = new ExtractedLabIndicator("Показатель", "10", null, null, null, null);
        var kbRange = new KbReferenceRange(AgeFrom: 18, AgeTo: 65, Sex: null, Low: 5, High: 8, Unit: null);

        var (flag, source, _, _) = IndicatorFlagCalculator.Calculate(indicator, kbFallback: kbRange, ageYears: 30, sex: null);
        flag.Should().Be(IndicatorFlag.High);
        source.Should().Be(RefSource.KbFixed);
    }

    [Fact]
    public void Calculate_NoBlankReference_KbRangeIgnoredWhenAgeOutOfBounds()
    {
        var indicator = new ExtractedLabIndicator("Показатель", "10", null, null, null, null);
        var kbRange = new KbReferenceRange(AgeFrom: 18, AgeTo: 65, Sex: null, Low: 5, High: 8, Unit: null);

        IndicatorFlagCalculator.Calculate(indicator, kbFallback: kbRange, ageYears: 10, sex: null).Flag.Should().Be(IndicatorFlag.Unknown);
    }

    [Fact]
    public void Calculate_KbRangeMatchesPatientSex_IsApplied()
    {
        var indicator = new ExtractedLabIndicator("Показатель", "10", null, null, null, null);
        var kbRange = new KbReferenceRange(AgeFrom: null, AgeTo: null, Sex: Gender.Female, Low: 5, High: 8, Unit: null);

        var (flag, source, _, _) = IndicatorFlagCalculator.Calculate(indicator, kbFallback: kbRange, ageYears: null, sex: Gender.Female);
        flag.Should().Be(IndicatorFlag.High);
        source.Should().Be(RefSource.KbFixed);
    }

    [Fact]
    public void Calculate_KbRangeForDifferentSex_IsIgnored()
    {
        var indicator = new ExtractedLabIndicator("Показатель", "10", null, null, null, null);
        var kbRange = new KbReferenceRange(AgeFrom: null, AgeTo: null, Sex: Gender.Male, Low: 5, High: 8, Unit: null);

        var (flag, source, _, _) = IndicatorFlagCalculator.Calculate(indicator, kbFallback: kbRange, ageYears: null, sex: Gender.Female);
        flag.Should().Be(IndicatorFlag.Unknown);
        source.Should().Be(RefSource.None);
    }

    /// <summary>Редизайн v2 — тест-страж контракта IndicatorDto.RefLowText/RefHighText (см.
    /// XML-докстринг там же): все три места записи (MedicalDocumentExtractionProcessor,
    /// ExtractionQueryService, RecalculateIndicatorFlagsJob) пишут EffectiveLow/EffectiveHigh как
    /// `.ToString(CultureInfo.InvariantCulture)` — фронт вправе делать parseFloat без
    /// нормализации запятых для шкалы-референса (PR4). Если это соглашение когда-нибудь
    /// разойдётся (например, кто-то начнёт форматировать текущей культурой с запятой), эта строка
    /// перестанет парситься на фронте молча — тест ловит расхождение здесь, на бэке.</summary>
    [Theory]
    [InlineData(130, 160)]
    [InlineData(3.3, 5.5)]
    [InlineData(0, 0.4)]
    [InlineData(-5, 10)]
    public void Calculate_BlankRange_EffectiveLowHigh_RoundTripThroughInvariantCultureString(double low, double high)
    {
        var indicator = new ExtractedLabIndicator("Показатель", "5", null, low, high, null);
        var (_, _, effLow, effHigh) = IndicatorFlagCalculator.Calculate(indicator, kbFallback: null, ageYears: null, sex: null);

        effLow.Should().NotBeNull();
        effHigh.Should().NotBeNull();

        var lowText = effLow!.Value.ToString(CultureInfo.InvariantCulture);
        var highText = effHigh!.Value.ToString(CultureInfo.InvariantCulture);

        // Запятая как разделитель сломала бы фронтовый parseFloat() ("5,6" → NaN).
        lowText.Should().NotContain(",");
        highText.Should().NotContain(",");

        double.TryParse(lowText, NumberStyles.Float, CultureInfo.InvariantCulture, out var roundTrippedLow).Should().BeTrue();
        double.TryParse(highText, NumberStyles.Float, CultureInfo.InvariantCulture, out var roundTrippedHigh).Should().BeTrue();
        roundTrippedLow.Should().Be(low);
        roundTrippedHigh.Should().Be(high);
    }
}
