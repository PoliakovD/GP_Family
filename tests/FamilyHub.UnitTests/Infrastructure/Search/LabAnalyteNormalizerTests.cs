using FamilyHub.Infrastructure.Search;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Search;

/// <summary>
/// Ветка medicalrecords: ключ дедупликации показателя (LabIndicator.AnalyteKey /
/// GlobalLabAnalyteKb.NormalizedName) должен быть устойчив к сокращению в скобках, единицам
/// измерения и артефактам OCR (латинские гомоглифы) — тот же принцип, что у
/// MedicationNameNormalizer, отдельный словарь под показатели анализов.
/// </summary>
public class LabAnalyteNormalizerTests
{
    [Theory]
    [InlineData("Гемоглобин (HGB), г/л", "гемоглобин")]
    [InlineData("Глюкоза, ммоль/л", "глюкоза")]
    [InlineData("Лейкоциты (WBC)", "лейкоциты")]
    [InlineData("Эритроциты, ×10^12/л", "эритроциты")]
    public void Normalize_StripsParentheticalsAndUnits(string raw, string expected)
    {
        LabAnalyteNormalizer.Normalize(raw).Should().Be(expected);
    }

    [Fact]
    public void Normalize_EmptyOrWhitespace_ReturnsEmpty()
    {
        LabAnalyteNormalizer.Normalize("   ").Should().BeEmpty();
        LabAnalyteNormalizer.Normalize(null).Should().BeEmpty();
    }

    [Fact]
    public void Normalize_MixedLatinCyrillicHomoglyphs_FixesWithinCyrillicWord()
    {
        // 'A' и 'O' латиницей внутри кириллического слова — типичный артефакт OCR.
        LabAnalyteNormalizer.Normalize("Гемoглoбин").Should().Be("гемоглобин");
    }

    [Fact]
    public void Normalize_PureLatinWord_NotMangled()
    {
        LabAnalyteNormalizer.Normalize("PSA общий").Should().Be("psa общий");
    }

    [Fact]
    public void Normalize_IsCaseInsensitiveAndTrims()
    {
        LabAnalyteNormalizer.Normalize("  ГЕМОГЛОБИН  ").Should().Be("гемоглобин");
    }

    [Theory]
    [InlineData("1. Гемоглобин", "гемоглобин")]
    [InlineData("12) Лейкоциты", "лейкоциты")]
    [InlineData("1.2 Белок", "белок")]
    [InlineData("5 Гемоглобин", "гемоглобин")]
    [InlineData("[0] Гемоглобин", "гемоглобин")]
    public void Normalize_StripsLeadingNumberingAndEchoIndex(string raw, string expected)
    {
        // Пересборка enrich-пайплайна: нумерация пункта бланка ("1. ") и эхо-подпись, которую
        // модель иногда возвращает вместе с исправленным текстом ("[0] "), не должны попадать в
        // ключ дедупликации — иначе один и тот же показатель на разных бланках расходится на
        // разные строки справочника.
        LabAnalyteNormalizer.Normalize(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("витамин B12", "витамин b12")]
    [InlineData("омега 3", "омега 3")]
    [InlineData("17-ОН-прогестерон", "17 он прогестерон")]
    public void Normalize_KeepsDigitsThatAreNotLeadingNumbering(string raw, string expected)
    {
        LabAnalyteNormalizer.Normalize(raw).Should().Be(expected);
    }
}
