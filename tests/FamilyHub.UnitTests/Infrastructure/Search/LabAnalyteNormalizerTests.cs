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
}
