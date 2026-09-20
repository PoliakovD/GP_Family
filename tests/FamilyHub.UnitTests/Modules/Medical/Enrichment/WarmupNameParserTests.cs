using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.Enrichment;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Enrichment;

/// <summary>Прогрев кэша веб-поиска из админки (грантовый лимит облака) — разбор textarea в
/// уникальные по нормализованному ключу имена, до вызова провайдера/БД.</summary>
public class WarmupNameParserTests
{
    [Fact]
    public void Parse_BlankAndWhitespaceLines_AreSkipped()
    {
        var result = WarmupNameParser.Parse("парацетамол\n\n   \nибупрофен\n", WebSearchTopic.Medication);

        result.Should().HaveCount(2);
        result.Select(n => n.Normalized).Should().Equal("парацетамол", "ибупрофен");
    }

    [Fact]
    public void Parse_DuplicatesAfterNormalization_KeepOnlyFirstRawForm()
    {
        // "Парацетамол 400мг таб. №20" и голое "Парацетамол" нормализуются в один и тот же ключ
        // (MedicationNameNormalizer снимает дозировку/фасовку/форму выпуска) — второй платный
        // вызов на то же название прогревом не оправдан.
        var result = WarmupNameParser.Parse(
            "Парацетамол 400мг таб. №20\nПарацетамол\nибупрофен", WebSearchTopic.Medication);

        result.Should().HaveCount(2);
        result[0].Raw.Should().Be("Парацетамол 400мг таб. №20");
        result[0].Normalized.Should().Be("парацетамол");
        result[1].Normalized.Should().Be("ибупрофен");
    }

    [Fact]
    public void Parse_LabAnalyteTopic_FoldsCrossAlphabetSynonymsToSameKey()
    {
        // Кросс-алфавитная свёртка (MedicalTextTransliterator.Fold внутри NormalizeAnalyteKey) —
        // "Adenovirus" и "аденовирус" должны прогреть кэш ОДНИМ ключом, не двумя платными вызовами.
        var result = WarmupNameParser.Parse("Adenovirus\nаденовирус", WebSearchTopic.LabAnalyte);

        result.Should().ContainSingle();
    }

    [Fact]
    public void Parse_LabAnalyteTopic_UsesFoldedKey_NotPlainNormalize()
    {
        var result = WarmupNameParser.Parse("Adenovirus", WebSearchTopic.LabAnalyte);

        result.Should().ContainSingle();
        result[0].Normalized.Should().Be("аденовирус");
    }

    [Fact]
    public void Parse_LineOverMaxLength_IsSkipped()
    {
        var tooLong = new string('а', 201);
        var result = WarmupNameParser.Parse($"{tooLong}\nпарацетамол", WebSearchTopic.Medication);

        result.Should().ContainSingle();
        result[0].Normalized.Should().Be("парацетамол");
    }

    [Fact]
    public void Parse_RespectsMaxNamesCap()
    {
        var lines = string.Join('\n', Enumerable.Range(0, 10).Select(i => $"препарат{i}"));

        var result = WarmupNameParser.Parse(lines, WebSearchTopic.Medication, maxNames: 3);

        result.Should().HaveCount(3);
    }

    [Fact]
    public void Parse_EmptyOrNullInput_ReturnsEmpty()
    {
        WarmupNameParser.Parse(null, WebSearchTopic.Medication).Should().BeEmpty();
        WarmupNameParser.Parse("   ", WebSearchTopic.Medication).Should().BeEmpty();
    }
}
