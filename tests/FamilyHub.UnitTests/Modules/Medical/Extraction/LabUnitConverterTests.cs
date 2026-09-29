using FamilyHub.Modules.Medical.Extraction;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>
/// Живой сценарий (план "качество ИИ-распознавания анализов", Этап 3): справочник даёт норму
/// глюкозы в ммоль/л, бланк лаборатории печатает результат в мг/дл — без конверсии
/// IndicatorFlagCalculator раньше сравнивал бы числа напрямую, игнорируя единицы измерения вовсе.
/// </summary>
public class LabUnitConverterTests
{
    [Fact]
    public void TryConvert_SameCanonicalUnit_ReturnsValueUnchanged()
    {
        var ok = LabUnitConverter.TryConvert(5.6, "ммоль/л", "ммоль/л", null, out var result);

        ok.Should().BeTrue();
        result.Should().Be(5.6);
    }

    [Fact]
    public void TryConvert_SameDimensionDifferentScale_ConvertsByScaleFactor_NoAnalyteKeyNeeded()
    {
        // 130 г/л → мг/дл: чистый масштаб, молярная масса не нужна вовсе.
        var ok = LabUnitConverter.TryConvert(130, "г/л", "мг/дл", null, out var result);

        ok.Should().BeTrue();
        result.Should().BeApproximately(13000, 0.001);
    }

    [Theory]
    [InlineData(90, "мг/дл", "ммоль/л", "глюкоза", 4.9955)] // клиническое правило: mg/dL / 18.0182 ≈ mmol/L
    [InlineData(1, "мг/дл", "µмоль/л", "креатинин", 88.41)] // клиническое правило: mg/dL × 88.4 ≈ µmol/L
    public void TryConvert_MassToMolar_KnownAnalyte_UsesMolarMass(
        double value, string fromUnit, string toUnit, string analyteKey, double expected)
    {
        var ok = LabUnitConverter.TryConvert(value, fromUnit, toUnit, analyteKey, out var result);

        ok.Should().BeTrue();
        result.Should().BeApproximately(expected, 0.05);
    }

    [Fact]
    public void TryConvert_MolarToMass_RoundTripsBackToOriginalValue()
    {
        LabUnitConverter.TryConvert(90, "мг/дл", "ммоль/л", "глюкоза", out var mmol).Should().BeTrue();

        var ok = LabUnitConverter.TryConvert(mmol, "ммоль/л", "мг/дл", "глюкоза", out var backToMgDl);

        ok.Should().BeTrue();
        backToMgDl.Should().BeApproximately(90, 0.01);
    }

    [Fact]
    public void TryConvert_MassToMolar_UnknownAnalyte_ReturnsFalse()
    {
        // Аналита нет в курируемой таблице молярных масс — приблизительная конверсия хуже, чем её
        // отсутствие (план: "если конвертация невозможна — норма не применяется", не гадаем).
        var ok = LabUnitConverter.TryConvert(5, "мг/дл", "ммоль/л", "неизвестный-редкий-показатель", out _);

        ok.Should().BeFalse();
    }

    [Fact]
    public void TryConvert_MassToMolar_NoAnalyteKeyGiven_ReturnsFalse()
    {
        var ok = LabUnitConverter.TryConvert(5, "мг/дл", "ммоль/л", null, out _);

        ok.Should().BeFalse();
    }

    [Theory]
    [InlineData("сек", "ммоль/л")]
    [InlineData("%", "мг/дл")]
    [InlineData("бла-бла", "мг/дл")]
    public void TryConvert_UnrecognizedOrNonConcentrationUnit_ReturnsFalse(string fromUnit, string toUnit)
    {
        var ok = LabUnitConverter.TryConvert(5, fromUnit, toUnit, "глюкоза", out _);

        ok.Should().BeFalse();
    }

    [Theory]
    [InlineData("ммоль/л", "mmol/l")]
    [InlineData("Ммоль/Л", "ммоль / л")]
    [InlineData("мкмоль/л", "µmol/l")]
    [InlineData("мкг/мл", "µg/ml")]
    public void TryConvert_SynonymSpellingsOfSameUnit_TreatedAsIdentical(string a, string b)
    {
        var ok = LabUnitConverter.TryConvert(1, a, b, null, out var result);

        ok.Should().BeTrue();
        result.Should().BeApproximately(1, 0.0001);
    }
}
