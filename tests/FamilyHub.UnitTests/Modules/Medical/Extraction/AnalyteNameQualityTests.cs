using FamilyHub.Modules.Medical.Extraction;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>Платный поиск не должен уходить по слову из шапки бланка или по обрезанному названию.</summary>
public class AnalyteNameQualityTests
{
    [Theory]
    [InlineData("Пациент")]
    [InlineData("пациент:")]
    [InlineData("Врач")]
    [InlineData("Средняя концентрация гемоглобина в")]
    [InlineData("Коэффициент вариации -")]
    [InlineData("Х")]
    [InlineData("")]
    public void RejectReason_BadNames_ReturnsReason(string name) =>
        AnalyteNameQuality.RejectReason(name).Should().NotBeNull();

    [Theory]
    [InlineData("Гемоглобин")]
    [InlineData("Средняя концентрация гемоглобина в эритроците (MCHC)")]
    [InlineData("Тромбоцитарный криТ")]
    [InlineData("АЧТВ")]
    [InlineData("Глюкоза, обнаружение в моче")]
    public void RejectReason_GoodNames_ReturnsNull(string name) =>
        AnalyteNameQuality.RejectReason(name).Should().BeNull();
}
