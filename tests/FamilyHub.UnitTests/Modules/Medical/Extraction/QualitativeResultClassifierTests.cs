using FamilyHub.Modules.Medical.Extraction;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>Живой баг: "не обнаружены" не совпадало с нормой "не обнаружено" ни числом, ни родом
/// (голое равенство строк) — результат молча уезжал в Unknown, хотя смысл совпадает (см.
/// IndicatorFlagCalculator). Классификатор сравнивает по корню слова + частице отрицания, не по
/// словарю словоформ.</summary>
public class QualitativeResultClassifierTests
{
    [Theory]
    [InlineData("не обнаружено")]
    [InlineData("не обнаружена")]
    [InlineData("не обнаружены")]
    [InlineData("отсутствуют")]
    [InlineData("отсутствует")]
    [InlineData("отрицательно")]
    [InlineData("Not detected")]
    [InlineData("negative")]
    public void Classify_NegativeFindingPhrasing_ReturnsNegativeFinding(string text)
    {
        QualitativeResultClassifier.Classify(text).Should().Be(QualitativePolarity.NegativeFinding);
    }

    [Theory]
    [InlineData("обнаружена")]
    [InlineData("обнаружены")]
    [InlineData("присутствуют")]
    [InlineData("слабо положительно")]
    [InlineData("positive")]
    [InlineData("detected")]
    public void Classify_PositiveFindingPhrasing_ReturnsPositiveFinding(string text)
    {
        QualitativeResultClassifier.Classify(text).Should().Be(QualitativePolarity.PositiveFinding);
    }

    [Fact]
    public void Classify_DoubleNegation_InvertsBackToPositive()
    {
        // Встречается на реальных бланках ("не отсутствуют" — двойное отрицание) — редкое, но
        // корень+частица обрабатывает его автоматически, без отдельного правила.
        QualitativeResultClassifier.Classify("не отсутствуют").Should().Be(QualitativePolarity.PositiveFinding);
    }

    [Theory]
    [InlineData("1-3 в п/зр")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("норма")]
    public void Classify_NotAQualitativeFinding_ReturnsUnknown(string? text)
    {
        QualitativeResultClassifier.Classify(text).Should().Be(QualitativePolarity.Unknown);
    }
}
