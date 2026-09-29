using FamilyHub.Modules.Medical.Extraction;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>
/// LabTableRowDetector решает "это строка-результат анализа или нет" на тексте, уже
/// реконструированном LayoutTextReconstructor (ячейки разделены " | "). Сценарии здесь — не
/// абстрактные, а прямые эхо реальных строк бланка Гемотест (13/13 показателей, проверено
/// вручную на живом файле при разработке, план "качество ИИ-распознавания анализов" Этап 1) и
/// главной цели детектора — НЕ спутать шапку документа (пациент, пол, дата рождения) со строкой
/// результата, даже когда она формально выглядит так же ("Имя | короткое значение").
/// </summary>
public class LabTableRowDetectorTests
{
    private const string Header = "Исследование | Результат | Ед. изм. | Реф. значения";

    [Fact]
    public void Detect_WellFormedTable_FindsAllResultRows()
    {
        var text = string.Join('\n',
            Header,
            "Гемоглобин | 118 | г/л | 130 - 160",
            "Эритроциты | 4.2 | 10^12/л | 3.8 - 5.1",
            "Лейкоциты | 6.5 | 10^9/л | 4.0 - 9.0");

        var result = LabTableRowDetector.Detect(text);

        result.Rows.Should().HaveCount(3);
        result.Rows.Select(r => r.RowId).Should().Equal("R1", "R2", "R3");
        result.Rows[0].RawLine.Should().Be("Гемоглобин | 118 | г/л | 130 - 160");
    }

    [Fact]
    public void Detect_PatientInfoBeforeTableHeader_IsNeverCountedAsResultRow()
    {
        // Живой баг: "Тестовна Теста Тестовична | Ж | 22.02.2002" в шапке документа формально
        // неотличимо от "Имя показателя | короткое значение" — единственное, что его отличает,
        // это позиция ДО первой строки заголовка таблицы (см. класс-докстринг: "табличная зона").
        var text = string.Join('\n',
            "Пациент",
            "Тестовна Теста Тестовична | Ж | 22.02.2002",
            Header,
            "Гемоглобин | 118 | г/л | 130 - 160");

        var result = LabTableRowDetector.Detect(text);

        result.Rows.Should().ContainSingle();
        result.Rows[0].RawLine.Should().Be("Гемоглобин | 118 | г/л | 130 - 160");
    }

    [Fact]
    public void Detect_SingleLetterValueCell_IsNeverTreatedAsQualitativeResult()
    {
        // Защита в глубину (не полагаемся только на позицию до заголовка): "Ж"/"М" — не
        // качественный результат ни при каких обстоятельствах, даже внутри табличной зоны.
        var text = string.Join('\n', Header, "Пол | Ж | -");

        var result = LabTableRowDetector.Detect(text);

        result.Rows.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Глюкоза | 4.41 | ммоль/л | 4.11 - 6.1")]
    [InlineData("Инсулин | ↓ 1.2 | мкЕд/мл | 2.2 - 25")] // стрелка тренда перед числом
    [InlineData("АТ-ТГ (кол.) | <0.9 | МЕ/мл | 0 - 4")] // цензурированное неравенство
    [InlineData("Индекс инсулинорезистентности HOMA-IR | 0.2 | < 2.7")] // без колонки единиц (3 ячейки)
    [InlineData("Билирубин общий | отрицательно | -")] // качественный результат словом
    public void Detect_RealisticResultRowShapes_AreDetected(string dataLine)
    {
        var result = LabTableRowDetector.Detect(Header + "\n" + dataLine);

        result.Rows.Should().ContainSingle();
        result.Rows[0].RawLine.Should().Be(dataLine);
    }

    [Fact]
    public void Detect_PanelHeaderWithoutValue_IsNotCountedAsResultRow()
    {
        // "МНО (+ПТВ и ПТИ)" — заголовок панели без значения, сами показатели идут отдельными
        // строками ниже (в т.ч. одна из них повторяет то же название, но уже со значением).
        var text = string.Join('\n',
            Header,
            "МНО (+ПТВ и ПТИ)",
            "Протромбиновое время | ↑ 13.1 | сек. | 9.4 - 12.5",
            "МНО (+ПТВ и ПТИ) | 1.10 | - | 0.8 - 1.14");

        var result = LabTableRowDetector.Detect(text);

        result.Rows.Should().HaveCount(2);
        result.PanelHeaderLines.Should().Be(1);
        result.Rows.Select(r => r.RawLine).Should().Equal(
            "Протромбиновое время | ↑ 13.1 | сек. | 9.4 - 12.5",
            "МНО (+ПТВ и ПТИ) | 1.10 | - | 0.8 - 1.14");
    }

    [Theory]
    [InlineData("Проба 1114789670 Вен.кровь Взятие Регистрация б/м 26.09.2026")]
    [InlineData("Метод: Иммунотурбидиметрический анализ")]
    [InlineData("Оборудование: Анализатор биохимический BS-800M1, Mindray")]
    [InlineData("Дата исследования: 26.09.2026")]
    [InlineData("1.50.")]
    [InlineData("26.2.")]
    [InlineData("* Для людей, не принимающих антикоагулянты")]
    public void Detect_KnownNoiseLines_AreNeverCountedAsResultRow(string noiseLine)
    {
        var result = LabTableRowDetector.Detect(Header + "\n" + noiseLine);

        result.Rows.Should().BeEmpty();
        result.NoiseLines.Should().Be(2); // строка заголовка + сама noise-строка
    }

    [Fact]
    public void Detect_JustifiedProseLineWithManyColumnGaps_IsTreatedAsNoiseNotResultRow()
    {
        // Дисклеймер мелким шрифтом внизу бланка — из-за выключки текста разрывы между словами
        // иногда превышают порог колонки в LayoutTextReconstructor и строка распадается на много
        // "ячеек"; результат анализа никогда не бывает из 5+ колонок.
        var prose = "Результат | лабораторного | исследования | не | является | медицинским | диагнозом";

        var result = LabTableRowDetector.Detect(Header + "\n" + prose);

        result.Rows.Should().BeEmpty();
    }

    [Fact]
    public void Detect_NoHeaderRowAnywhere_FindsNoResultRows()
    {
        // Нестандартная форма без узнаваемой шапки таблицы — по плану (Этап 1, пункт 5) это сигнал
        // для fallback-прохода analysis.enumerate-rows, а не повод угадывать строки вслепую.
        var text = "Гемоглобин | 118 | г/л | 130 - 160\nЭритроциты | 4.2 | 10^12/л | 3.8 - 5.1";

        var result = LabTableRowDetector.Detect(text);

        result.Rows.Should().BeEmpty();
    }

    /// <summary>Протокол медорганизации (выписка из ЕМИАС/МИС): колонки "Дата | Показатель |
    /// Значение | Референс", название показателя переносится на несколько строк узкой колонки,
    /// запись может перетекать через границу страницы, внизу каждой страницы блок ЭЦП. Текст —
    /// синтетический, повторяет только раскладку.</summary>
    private const string DatedProtocol = """
        Протокол лабораторного исследования
        Пациент: Тестовна Теста Тестовична (Женский), дата рождения: 22.02.2002
        Референтный
        Дата | Показатель | Значение
        диапазон
        Отдельные лабораторные тесты
        01.01.2026 10:00 | Тестин, | +
        обнаружение в
        осадке пробы
        01.01.2026 10:00 | Тестозид, | 2-4
        количество в поле
        зрения в осадке
        PDF-представление подписано электронной подписью: | Оригинальный электронный документ подписан
        ПОДПИСАНО ЭЛЕКТРОННОЙ ПОДПИСЬЮ | ПОДПИСАНО ЭЛЕКТРОННОЙ ПОДПИСЬЮ
        Владелец: ТЕСТОВОЕ УЧРЕЖДЕНИЕ
        ДОМ"
        Протокол лабораторного исследования 1.2.3 страница 1 из 2

        --- стр. 2 ---

        пробы методом
        микроскопии
        01.01.2026 10:00 | Тестоген, | 0 г/л | 0-0.1
        обнаружение в
        пробе
        01.01.2026 10:00 | Тестоген, | не обнаружен
        обнаружение в | ммоль/л
        пробе
        01.01.2026 10:00 | Реакция пробы | 5,5
        Оказанные услуги
        B03.016.006 Тестовая услуга от 01.01.2026
        Исполнители:
        Тестовый техник, ТЕСТОВ ТЕСТ ТЕСТОВИЧ
        """;

    [Fact]
    public void Detect_DatedMultiLineTable_JoinsWrappedNamesAcrossPagesAndIgnoresFooters()
    {
        var result = LabTableRowDetector.Detect(DatedProtocol);

        result.Rows.Select(r => r.Cells[1]).Should().Equal(
            "Тестин, обнаружение в осадке пробы",
            "Тестозид, количество в поле зрения в осадке пробы методом микроскопии",
            "Тестоген, обнаружение в пробе",
            "Тестоген, обнаружение в пробе",
            "Реакция пробы");
        result.Rows.Should().OnlyContain(r => r.NameCellIndex == 1);
    }

    [Fact]
    public void Detect_DatedMultiLineTable_KeepsValueUnitAndReference()
    {
        var rows = LabTableRowDetector.Detect(DatedProtocol).Rows;

        rows[0].Cells[2].Should().Be("+");
        rows[2].RawLine.Should().Be("01.01.2026 10:00 | Тестоген, обнаружение в пробе | 0 г/л | 0-0.1");
        rows[3].Cells[2].Should().Be("не обнаружен ммоль/л", "единица из узкой колонки уходит к значению, а не в название");
        rows[4].Cells[2].Should().Be("5,5");
    }

    [Fact]
    public void Detect_DatedMultiLineTable_TrailingSectionsAfterTableAreNotRows()
    {
        var rows = LabTableRowDetector.Detect(DatedProtocol).Rows;

        rows.Should().HaveCount(5);
        rows.Should().NotContain(r => r.RawLine.Contains("услуга") || r.RawLine.Contains("ПОДПИСАНО"));
    }

    [Fact]
    public void Detect_EmptyInput_ReturnsEmptyResult()
    {
        var result = LabTableRowDetector.Detect(string.Empty);

        result.Rows.Should().BeEmpty();
        result.PanelHeaderLines.Should().Be(0);
        result.NoiseLines.Should().Be(0);
    }

    [Fact]
    public void Detect_RowIds_AreSequentialAcrossWholeDocument()
    {
        var text = string.Join('\n',
            Header,
            "Гемоглобин | 118 | г/л | 130 - 160",
            "--- стр. 2 ---",
            Header,
            "Эритроциты | 4.2 | 10^12/л | 3.8 - 5.1");

        var result = LabTableRowDetector.Detect(text);

        result.Rows.Select(r => r.RowId).Should().Equal("R1", "R2");
    }
}
