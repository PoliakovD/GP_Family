using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Extraction;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>TECH_DEBT #19: "Нейтрофилы | 55 | %" и "Нейтрофилы | 3.1 | 10^9/л" одного бланка — без разводки
/// один ключ, и вторая строка перезаписывала первую.</summary>
public class PercentAbsoluteSplitterTests
{
    private static readonly Guid File1 = Guid.NewGuid();
    private static readonly Guid File2 = Guid.NewGuid();
    private static readonly string Percent = LabAnalyteNormalizer.NormalizeAnalyteKey("нейтрофилы");
    private static readonly string Absolute = LabAnalyteNormalizer.NormalizeAnalyteKey("нейтрофилы абс");

    private static (ExtractedLabIndicator, Guid) Row(string name, string value, string? unit, Guid file) =>
        (new ExtractedLabIndicator(name, value, unit, null, null, null), file);

    private static List<string> Keys(IEnumerable<(ExtractedLabIndicator Dto, Guid FileGroupId)> rows) =>
        rows.Select(r => LabAnalyteNormalizer.NormalizeAnalyteKey(r.Dto.Name)).ToList();

    [Fact]
    public void SameNamePercentAndCount_OnOneBlank_AbsoluteGetsOwnKey()
    {
        var result = PercentAbsoluteSplitter.Apply([
            Row("Нейтрофилы", "55", "%", File1),
            Row("Нейтрофилы", "3.1", "10^9/л", File1),
        ]);

        result[0].Dto.Name.Should().Be("Нейтрофилы");
        result[1].Dto.Name.Should().Be("Нейтрофилы, абс.");
        Keys(result).Should().Equal(Percent, Absolute);
    }

    [Fact]
    public void PercentMarkedInName_IsRecognizedAsPercent()
    {
        var result = PercentAbsoluteSplitter.Apply([
            Row("Нейтрофилы (общ.число), %", "46.2", null, File1),
            Row("Нейтрофилы (общ.число)", "3.3", "тыс/мкл", File1),
        ]);

        Keys(result).Should().Equal(Percent, Absolute);
    }

    [Fact]
    public void DifferentFiles_AreNotSplit()
    {
        // Разные файлы разводит AnalyteKeyDisambiguator (по файлу), не эта разводка.
        var result = PercentAbsoluteSplitter.Apply([
            Row("Нейтрофилы", "55", "%", File1),
            Row("Нейтрофилы", "3.1", "10^9/л", File2),
        ]);

        result.Select(r => r.Dto.Name).Should().AllBe("Нейтрофилы");
    }

    [Fact]
    public void SingleRowOrNoPercentSibling_IsUntouched()
    {
        var result = PercentAbsoluteSplitter.Apply([
            Row("Лейкоциты", "6.5", "10^9/л", File1),
            Row("Гематокрит", "42", "%", File1),
            Row("Нейтрофилы", "3.1", "10^9/л", File1),
        ]);

        result.Select(r => r.Dto.Name).Should().Equal("Лейкоциты", "Гематокрит", "Нейтрофилы");
    }

    [Fact]
    public void ExplicitAbsoluteName_AlreadyHasOwnKey_NoDoubleSuffix()
    {
        var result = PercentAbsoluteSplitter.Apply([
            Row("Нейтрофилы, %", "55", "%", File1),
            Row("Нейтрофилы (NEU#)", "3.1", "10^9/л", File1),
        ]);

        result[1].Dto.Name.Should().Be("Нейтрофилы (NEU#)");
        Keys(result).Should().Equal(Percent, Absolute);
    }
}
