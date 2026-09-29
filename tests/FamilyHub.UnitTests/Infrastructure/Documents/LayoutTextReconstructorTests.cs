using FamilyHub.Infrastructure.Documents;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Documents;

/// <summary>
/// LayoutTextReconstructor восстанавливает читаемый порядок текста табличного бланка по
/// координатам слов вместо порядка операторов отрисовки PDF (см. класс-докстринг: живой пример —
/// бланк Гемотест, где референсный диапазон в потоке PDF идёт ПЕРЕД значением, хотя визуально
/// после). Координаты здесь синтетические (без реального PDF/PdfPig) — воспроизводят сетку
/// табличного бланка анализов: строка = слова с близким Y, ячейка = слова с небольшим
/// горизонтальным разрывом, граница колонки = разрыв в несколько высот строки.
/// </summary>
public class LayoutTextReconstructorTests
{
    private const double RowHeight = 10; // Top - Bottom одного слова обычного бланка
    private const double NormalWordGap = 4; // разрыв между словами одной фразы/ячейки
    private const double ColumnGap = 30; // разрыв между колонками таблицы (> порога)

    [Fact]
    public void Reconstruct_EmptyInput_ReturnsEmptyString()
    {
        LayoutTextReconstructor.Reconstruct([]).Should().BeEmpty();
    }

    [Fact]
    public void Reconstruct_SingleLine_JoinsWordsWithSpaces()
    {
        // "Гемоглобин 118 г/л" — обычные пробелы внутри фразы, границы колонки нет.
        var words = new List<PositionedWord>
        {
            Word("Гемоглобин", left: 10, width: 60, centerY: 100),
            Word("118", left: 74, width: 20, centerY: 100),
            Word("г/л", left: 98, width: 20, centerY: 100),
        };

        LayoutTextReconstructor.Reconstruct(words).Should().Be("Гемоглобин 118 г/л");
    }

    [Fact]
    public void Reconstruct_WordsOnDifferentY_ProducesSeparateLines()
    {
        var words = new List<PositionedWord>
        {
            Word("Первая", left: 10, width: 40, centerY: 200),
            Word("строка", left: 54, width: 40, centerY: 200),
            Word("Вторая", left: 10, width: 40, centerY: 186), // на 14pt ниже — новая строка
            Word("строка", left: 54, width: 40, centerY: 186),
        };

        var result = LayoutTextReconstructor.Reconstruct(words);

        result.Should().Be("Первая строка\nВторая строка");
    }

    /// <summary>Ключевой сценарий: имитирует реальный бланк Гемотест, где в потоке содержимого PDF
    /// значение/единица/референс одной строки таблицы идут в другом порядке, чем визуально —
    /// массив слов намеренно перемешан (не в порядке чтения), реконструкция обязана вернуть
    /// правильный визуальный порядок "Название | Значение | Ед.изм. | Референс" по координатам,
    /// а не по порядку элементов входного списка.</summary>
    [Fact]
    public void Reconstruct_WordsOutOfStreamOrder_RestoresVisualReadingOrder()
    {
        const double y = 100;
        var refRange = new[]
        {
            Word("4.11", left: 230, width: 20, centerY: y),
            Word("-", left: 253, width: 3, centerY: y),
            Word("6.1", left: 259, width: 16, centerY: y),
        };
        var unit = Word("ммоль/л", left: 150, width: 40, centerY: y);
        var value = Word("4.41", left: 100, width: 20, centerY: y);
        var name = Word("Глюкоза", left: 10, width: 50, centerY: y);

        // Порядок в списке — как в потоке PDF (референс раньше значения), не визуальный.
        var scrambled = new List<PositionedWord> { refRange[0], refRange[1], refRange[2], unit, value, name };

        LayoutTextReconstructor.Reconstruct(scrambled).Should().Be("Глюкоза | 4.41 | ммоль/л | 4.11 - 6.1");
    }

    [Fact]
    public void Reconstruct_MultiWordCellWithSmallGaps_StaysOneCell()
    {
        // "С-реактивный белок (СРБ)" — одна ячейка из трёх слов с обычными межсловными пробелами,
        // затем реальная граница колонки перед значением.
        var words = new List<PositionedWord>
        {
            Word("С-реактивный", left: 10, width: 70, centerY: 100),
            Word("белок", left: 84, width: 35, centerY: 100),
            Word("(СРБ)", left: 123, width: 30, centerY: 100),
            Word("1.01", left: 183, width: 20, centerY: 100), // граница колонки: разрыв 30pt
        };

        LayoutTextReconstructor.Reconstruct(words).Should().Be("С-реактивный белок (СРБ) | 1.01");
    }

    [Fact]
    public void Reconstruct_TableWithPanelHeaderAndDataRows_ProducesOneLinePerRow()
    {
        // Панельный заголовок ("Индекс инсулинорезистентности HOMA-IR" без значения) НЕ должен
        // слиться со строкой данных ниже — это разные Y, разные строки вывода.
        var header = Cell("Индекс инсулинорезистентности HOMA-IR", left: 10, centerY: 128);
        var row1 = new[]
        {
            Cell("Глюкоза", left: 10, centerY: 114),
            Word("4.41", left: 100, width: 20, centerY: 114),
            Word("ммоль/л", left: 150, width: 40, centerY: 114),
        };
        var row2 = new[]
        {
            Cell("Инсулин", left: 10, centerY: 100),
            Word("1.2", left: 100, width: 20, centerY: 100),
            Word("мкЕд/мл", left: 150, width: 40, centerY: 100),
        };

        var words = new List<PositionedWord> { header }.Concat(row1).Concat(row2).ToList();

        var result = LayoutTextReconstructor.Reconstruct(words);

        result.Should().Be(
            "Индекс инсулинорезистентности HOMA-IR\n" +
            "Глюкоза | 4.41 | ммоль/л\n" +
            "Инсулин | 1.2 | мкЕд/мл");
    }

    [Fact]
    public void Reconstruct_SmallGapBelowThreshold_DoesNotSplitColumn()
    {
        // Разрыв чуть меньше порога (ColumnGapFactor*height) — должен остаться обычным пробелом,
        // не границей колонки.
        var words = new List<PositionedWord>
        {
            Word("Итого", left: 10, width: 30, centerY: 100),
            Word("норма", left: 40 + (RowHeight * 1.8) - 2, width: 30, centerY: 100), // gap = threshold - 2
        };

        LayoutTextReconstructor.Reconstruct(words).Should().Be("Итого норма");
    }

    private static PositionedWord Word(string text, double left, double width, double centerY) =>
        new(text, left, left + width, centerY + RowHeight / 2, centerY - RowHeight / 2);

    /// <summary>Однословная "ячейка" — удобный алиас Word для читаемости тестов таблицы.</summary>
    private static PositionedWord Cell(string text, double left, double centerY) =>
        Word(text, left, width: text.Length * 6, centerY);
}
