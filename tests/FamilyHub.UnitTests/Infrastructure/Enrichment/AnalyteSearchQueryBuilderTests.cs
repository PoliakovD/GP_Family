using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.Prompts;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Enrichment;

/// <summary>AnalyteSearchQueryBuilder — шаблон поискового запроса для LabAnalyte редактируется из
/// админки через IPromptProvider (ключ "analysis.search-query"), поэтому проверяем именно
/// подстановку плейсхолдеров {name}/{specimen} в шаблон, а не сам IPromptProvider (его уже
/// покрывает PromptProviderTests).</summary>
public class AnalyteSearchQueryBuilderTests
{
    private static AnalyteSearchQueryBuilder CreateSut(string template)
    {
        var promptProvider = Substitute.For<IPromptProvider>();
        promptProvider.GetAsync("analysis.search-query", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(template));
        return new AnalyteSearchQueryBuilder(promptProvider);
    }

    [Fact]
    public async Task BuildAsync_WithSpecimen_SubstitutesBothPlaceholders()
    {
        var sut = CreateSut(AnalyteSearchQueryBuilder.FallbackTemplate);

        var result = await sut.BuildAsync("гемоглобин", "кровь");

        result.Should().Be("гемоглобин (кровь) анализ норма референсные значения у мужчин и женщин по возрасту единицы измерения");
    }

    [Fact]
    public async Task BuildAsync_WithoutSpecimen_LeavesNoDanglingSpaceOrParens()
    {
        var sut = CreateSut(AnalyteSearchQueryBuilder.FallbackTemplate);

        var result = await sut.BuildAsync("гемоглобин", null);

        result.Should().Be("гемоглобин анализ норма референсные значения у мужчин и женщин по возрасту единицы измерения");
    }

    [Fact]
    public async Task BuildAsync_CustomTemplateFromAdmin_IsUsedVerbatim()
    {
        var sut = CreateSut("норма {name}{specimen} — только диапазон, без пояснений");

        var result = await sut.BuildAsync("глюкоза", "плазма");

        result.Should().Be("норма глюкоза (плазма) — только диапазон, без пояснений");
    }
}
