using FamilyHub.Modules.Medical.Search;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical;

/// <summary>
/// Этап 3: in-memory поиск по зашифрованным медкартам должен находить словоформы («анализ» ↔
/// «анализы») и опечатки OCR («гемоглабин» → «гемоглобин»), как это делает Postgres-FTS для
/// незашифрованных данных (ADR-0003).
/// </summary>
public class RussianTextSearcherTests
{
    private readonly IRussianTextSearcher _sut = new RussianTextSearcher();

    [Theory]
    [InlineData("Общий анализ крови", "анализ")]
    [InlineData("Общий анализ крови", "анализы")]
    [InlineData("Общий анализ крови", "анализов")]
    [InlineData("Результаты анализов на гормоны", "анализ")]
    [InlineData("Приём у врача-кардиолога", "врачу")]
    [InlineData("Консультация кардиолога", "кардиолог")]
    public void Score_MatchesRussianWordForms(string text, string query)
    {
        _sut.Score(text, query).Should().BeGreaterThan(0, "словоформа «{0}» должна находиться в «{1}»", query, text);
    }

    [Theory]
    [InlineData("Уровень гемоглобина понижен", "гемоглабин")]
    [InlineData("Направление к эндокринологу", "эндокринолог")]
    public void Score_TypoTolerant_MatchesOcrLikeMisspelling(string text, string query)
    {
        _sut.Score(text, query).Should().BeGreaterThan(0, "опечатка OCR в «{0}» всё равно должна находить «{1}»", query, text);
    }

    [Theory]
    [InlineData("Общий анализ крови", "рентген")]
    [InlineData("Консультация кардиолога", "стоматолог")]
    public void Score_UnrelatedQuery_ReturnsZero(string text, string query)
    {
        _sut.Score(text, query).Should().Be(0);
    }

    [Fact]
    public void Score_MultiWordQuery_RequiresAllWordsToMatch_AndSemantics()
    {
        // "анализ" совпадает, "рентген" — нет: весь запрос не должен считаться совпавшим (AND).
        _sut.Score("Общий анализ крови", "анализ рентген").Should().Be(0);
    }

    [Fact]
    public void Score_MultiWordQuery_AllWordsMatch_ReturnsPositiveScore()
    {
        _sut.Score("Общий анализ крови на гормоны", "анализ гормоны").Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(null, "анализ")]
    [InlineData("", "анализ")]
    [InlineData("Общий анализ крови", null)]
    [InlineData("Общий анализ крови", "")]
    public void Score_EmptyOrNullInput_ReturnsZero(string? text, string? query)
    {
        _sut.Score(text, query).Should().Be(0);
    }

    [Fact]
    public void Score_IdenticalWord_ReturnsMaximumScore()
    {
        _sut.Score("гормон", "гормон").Should().Be(1);
    }
}
