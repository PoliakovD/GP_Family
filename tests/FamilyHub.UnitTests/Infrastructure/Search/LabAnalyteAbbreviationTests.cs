using FamilyHub.Infrastructure.Search;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Search;

/// <summary>Модель и справочник приводят аббревиатуры к "литературному" виду ("АЧТВ" → "Ачтв") —
/// RestoreAbbreviations возвращает написание из бланка, когда имя то же с точностью до регистра.</summary>
public class LabAnalyteAbbreviationTests
{
    [Theory]
    [InlineData("Ачтв", "АЧТВ", "АЧТВ")]
    [InlineData("Срб", "СРБ", "СРБ")]
    [InlineData("С-реактивный белок (срб)", "С-реактивный белок (СРБ)", "С-реактивный белок (СРБ)")]
    [InlineData("Мно (+птв и пти)", "МНО (+ПТВ и ПТИ)", "МНО (+ПТВ и ПТИ)")]
    public void RestoreAbbreviations_SameNameDifferentCase_ReturnsFormSpelling(string candidate, string source, string expected)
    {
        LabAnalyteNameCleaner.RestoreAbbreviations(candidate, source).Should().Be(expected);
    }

    [Theory]
    [InlineData("Глюкоза", "Глюкоза")] // в бланке нет аббревиатуры
    [InlineData("Инсулин", "Инсулин")]
    public void RestoreAbbreviations_NoAbbreviationInSource_KeepsCandidate(string candidate, string source)
    {
        LabAnalyteNameCleaner.RestoreAbbreviations(candidate, source).Should().Be(candidate);
    }

    [Fact]
    public void RestoreAbbreviations_DifferentNames_KeepsCandidate()
    {
        LabAnalyteNameCleaner.RestoreAbbreviations("Фибриноген", "АЧТВ").Should().Be("Фибриноген");
    }
}
