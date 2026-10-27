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
    public void Detect_OutOfRangeValuesMarkedWithAsterisk_AreDetected()
    {
        // Живой баг (бланк «Бьютимед»: колонки Исследование | Результат | Единицы | Референс | Комментарий): значения
        // вне нормы печатаются со звёздочкой («50.0*»), и такие строки — самые важные для пользователя — отбрасывались
        // как «вода». Звёздочка-сноска внизу таблицы («* Результат, выходящий…») по-прежнему шумит.
        var text = string.Join('\n',
            "Исследование | Результат | Единицы | Референсные | Комментарий",
            "значения",
            "Гематокрит | 50.0* | % | 39.0 - 49.0",
            "Гемоглобин | 17.3 | г/дл | 13.2 - 17.3",
            "Эритроциты | 5.94* | млн/мкл | 4.30 - 5.70",
            "Лимфоциты, % | 41.5* | % | 19.0 - 37.0",
            "Базофилы, % | 0.3 | % | < 1.0",
            "* Результат, выходящий за пределы референсных значений");

        var result = LabTableRowDetector.Detect(text);

        result.Rows.Select(r => r.Cells[0]).Should().Equal("Гематокрит", "Гемоглобин", "Эритроциты", "Лимфоциты, %", "Базофилы, %");
    }

    [Fact]
    public void Detect_RowWithCommentColumn_IsDetected_AndWrappedCommentLinesAreNot()
    {
        // Пятая колонка «Комментарий» переносится на несколько строк: сама строка результата — 5 ячеек,
        // хвост названия («(общ.число), %») приклеивается к имени, а остаток комментария строкой результата не считается.
        var text = string.Join('\n',
            "Исследование | Результат | Единицы | Референсные | Комментарий",
            "Лейкоциты | 7.14 | тыс/мкл | 4.50 - 11.00",
            "Нейтрофилы | 46.2* | % | 48.0 - 78.0 | При исследовании крови на гематологическом",
            "(общ.число), % | анализаторе патологических клеток не",
            "обнаружено. Количество палочкоядерных",
            "нейтрофилов не превышает 6%",
            "Лимфоциты, % | 41.5* | % | 19.0 - 37.0");

        var result = LabTableRowDetector.Detect(text);

        result.Rows.Select(r => r.Cells[0]).Should().Equal("Лейкоциты", "Нейтрофилы (общ.число), %", "Лимфоциты, %");
        result.Rows[1].RawLine.Should().StartWith("Нейтрофилы (общ.число), % | 46.2* | % | 48.0 - 78.0");
    }

    [Fact]
    public void Detect_NameWrappedToNextLine_IsJoinedBackIntoOneName()
    {
        // Живой баг: в узкой колонке название переносится («MCH (ср. содер. Hb в» / «эр.)»), а вторая строка —
        // одна ячейка — терялась, и в таблице анализа имя оставалось обрезанным.
        var text = string.Join('\n',
            "Исследование | Результат | Единицы | Референсные | Комментарий",
            "RDW (шир. распред. | 11.8 | % | 11.6 - 14.8",
            "эритр)",
            "MCH (ср. содер. Hb в | 29.1 | пг | 27.0 - 34.0",
            "эр.)",
            "Тромбоциты | 298 | тыс/мкл | 150 - 400");

        var result = LabTableRowDetector.Detect(text);

        result.Rows.Select(r => r.Cells[0]).Should().Equal("RDW (шир. распред. эритр)", "MCH (ср. содер. Hb в эр.)", "Тромбоциты");
        result.Rows[1].RawLine.Should().Be("MCH (ср. содер. Hb в эр.) | 29.1 | пг | 27.0 - 34.0");
    }

    [Fact]
    public void Detect_LineAfterRow_ThatIsItselfAResultRow_IsNotSwallowedAsNameContinuation()
    {
        var text = string.Join('\n',
            "Исследование | Результат | Единицы | Референсные",
            "Панель (общая | 5 | г/л | 1 - 9",
            "(скобка) | 7 | г/л | 1 - 9");

        var result = LabTableRowDetector.Detect(text);

        result.Rows.Should().HaveCount(2, "строка со значением во второй ячейке — самостоятельный результат");
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

    /// <summary>Название заполнило узкую колонку во всю ширину — LayoutTextReconstructor склеил его со значением
    /// («Натрий, молярная 138,8 ммоль/л | 130-156»), и референс встал на место значения. Раскладка — с реального
    /// протокола ГБУЗ РК (биохимия, 13 показателей): до фикса модель брала «&lt;35» значением АСТ вместо 11.</summary>
    private const string DatedProtocolWithGluedValues = """
        Референтный
        Дата | Показатель | Значение
        диапазон
        Отдельные лабораторные тесты
        25.05.2026 12:54 | Глюкоза, | 4,41 ммоль/л | 3.3-6.6
        молярная
        концентрация в
        венозной крови
        25.05.2026 12:54 | Натрий, молярная 138,8 ммоль/л | 130-156
        концентрация в
        венозной крови
        25.05.2026 12:54 | Калий, молярная 3,52 ммоль/л | 3.5-5.4
        концентрация в
        венозной крови
        25.05.2026 12:54 | Аспартатаминотра 11 МЕ/л | <35
        нсфераза,
        каталитическая
        концентрация в
        сыворотке или
        плазме крови
        25.05.2026 12:54 | Витамин B12 | 300 | 187-883
        Оказанные услуги
        """;

    [Fact]
    public void Detect_DatedRowWithValueGluedIntoName_SplitsValueOutAndKeepsReference()
    {
        var rows = LabTableRowDetector.Detect(DatedProtocolWithGluedValues).Rows;

        rows.Select(r => r.RawLine).Should().Equal(
            "25.05.2026 12:54 | Глюкоза, молярная концентрация в венозной крови | 4,41 ммоль/л | 3.3-6.6",
            "25.05.2026 12:54 | Натрий, молярная концентрация в венозной крови | 138,8 ммоль/л | 130-156",
            "25.05.2026 12:54 | Калий, молярная концентрация в венозной крови | 3,52 ммоль/л | 3.5-5.4",
            "25.05.2026 12:54 | Аспартатаминотра нсфераза, каталитическая концентрация в сыворотке или плазме крови | 11 МЕ/л | <35",
            "25.05.2026 12:54 | Витамин B12 | 300 | 187-883");
    }

    [Theory]
    [InlineData("25.05.2026 12:54 | Витамин B12 | 300")]
    [InlineData("25.05.2026 12:54 | Тестозид, | 2-4")]
    public void Detect_DatedRowWithoutGluedUnit_IsNotSplit(string line)
    {
        var rows = LabTableRowDetector.Detect("Дата | Показатель | Значение | Референс\n" + line).Rows;

        rows.Should().ContainSingle().Which.RawLine.Should().Be(line,
            "без единицы после числа хвост названия не считается значением («Витамин B12»)");
    }

    [Fact]
    public void Detect_WrappedNameTailOnNextLine_IsMergedIntoTheRowName()
    {
        var text = string.Join('\n',
            Header,
            "Средняя концентрация гемоглобина в | 330 | г/л | 300 - 360",
            "эритроците",
            "Гематокрит | 40 | % | 35 - 45",
            "МНО (+ПТВ и ПТИ)",
            "Протромбиновое время | 13 | сек. | 9 - 12");

        var rows = LabTableRowDetector.Detect(text).Rows;

        rows.Select(r => r.Cells[0]).Should().Equal(
            "Средняя концентрация гемоглобина в эритроците", "Гематокрит", "Протромбиновое время");
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

    [Fact]
    public void Detect_PanelHeaders_AreAttachedToFollowingRows()
    {
        // Заголовок раздела над шапкой колонок (как у Гемотест) и второй раздел внутри таблицы.
        var text = string.Join('\n',
            "Общий анализ крови",
            Header,
            "Гемоглобин | 118 | г/л | 130 - 160",
            "Лейкоциты | 6.5 | 10^9/л | 4.0 - 9.0",
            "ЛЕЙКОЦИТАРНАЯ ФОРМУЛА",
            "Нейтрофилы, % | 55 | % | 47 - 72",
            "Лимфоциты, % | 30 | % | 19 - 37");

        var result = LabTableRowDetector.Detect(text);

        result.Rows.Select(r => r.PanelLabel).Should().Equal(
            "Общий анализ крови", "Общий анализ крови", "ЛЕЙКОЦИТАРНАЯ ФОРМУЛА", "ЛЕЙКОЦИТАРНАЯ ФОРМУЛА");
    }

    [Fact]
    public void Detect_PanelSurvivesPageBreakAndRepeatedHeader()
    {
        var text = string.Join('\n',
            "Биохимический анализ крови",
            Header,
            "Глюкоза | 4.41 | ммоль/л | 4.11 - 6.1",
            "--- стр. 2 ---",
            Header,
            "Креатинин | 80 | мкмоль/л | 62 - 106");

        var result = LabTableRowDetector.Detect(text);

        result.Rows.Select(r => r.PanelLabel).Should().AllBe("Биохимический анализ крови");
    }

    [Fact]
    public void Detect_DocumentHeaderCaptions_AreNotPanels()
    {
        // "Пациент" над строкой ФИО — подпись шапки документа, не раздел; без разделов на бланке
        // PanelLabel остаётся null (таблица как раньше).
        var text = string.Join('\n',
            "Пациент",
            "Тестовна Теста Тестовична | Ж | 22.02.2002",
            Header,
            "Гемоглобин | 118 | г/л | 130 - 160");

        var result = LabTableRowDetector.Detect(text);

        result.Rows.Should().ContainSingle().Which.PanelLabel.Should().BeNull();
    }

    [Fact]
    public void Detect_PatientCaptionWithoutStopWord_IsResetByPatientRow()
    {
        // Подпись шапки, которой нет в стоп-листе, всё равно не становится разделом: многоячеечная
        // строка-шум ДО таблицы (сведения о пациенте) её сбрасывает.
        var text = string.Join('\n',
            "Сведения о заказе",
            "Тестовна Теста Тестовична | Ж | 22.02.2002",
            Header,
            "Гемоглобин | 118 | г/л | 130 - 160");

        LabTableRowDetector.Detect(text).Rows.Single().PanelLabel.Should().BeNull();
    }

    [Theory]
    [InlineData("Половые гормоны")] // "Пол" в стоп-листе — только целым словом
    [InlineData("Гормоны щитовидной железы")]
    [InlineData("HOMA-IR")]
    public void Detect_PanelHeaderVariants_AreRecognized(string panel)
    {
        var text = string.Join('\n', Header, panel, "Показатель Х | 5 | ед | 1 - 9");

        LabTableRowDetector.Detect(text).Rows.Single().PanelLabel.Should().Be(panel);
    }

    [Theory]
    [InlineData("Комментарий:")]
    [InlineData("Результат подтверждён повторно.")]
    [InlineData("Дата взятия биоматериала")]
    public void Detect_CaptionsAndNotes_AreNotPanels(string line)
    {
        var text = string.Join('\n', Header, line, "Показатель Х | 5 | ед | 1 - 9");

        LabTableRowDetector.Detect(text).Rows.Single().PanelLabel.Should().BeNull();
    }

    [Fact]
    public void Detect_NomenclatureCode_ClosesPreviousServicePanel()
    {
        // Гемотест: каждая услуга начинается с кода номенклатуры — панель "HOMA-IR" не должна
        // перейти на следующий одиночный показатель другой услуги.
        var text = string.Join('\n',
            Header,
            "Индекс инсулинорезистентности HOMA-IR",
            "Глюкоза | 4.41 | ммоль/л | 4.11 - 6.1",
            "Инсулин | 1.2 | мкЕд/мл | 2.2 - 25",
            "6.10.",
            "С-реактивный белок (СРБ) | 1.01 | мг/л | < 5");

        var result = LabTableRowDetector.Detect(text);

        result.Rows.Select(r => r.PanelLabel).Should().Equal(
            "Индекс инсулинорезистентности HOMA-IR", "Индекс инсулинорезистентности HOMA-IR", null);
    }

    [Fact]
    public void Detect_TableEnd_ClosesPanel()
    {
        var text = string.Join('\n',
            "Общий анализ крови",
            Header,
            "Гемоглобин | 118 | г/л | 130 - 160",
            "Оказанные услуги",
            Header,
            "Глюкоза | 4.41 | ммоль/л | 4.11 - 6.1");

        LabTableRowDetector.Detect(text).Rows.Select(r => r.PanelLabel).Should().Equal("Общий анализ крови", null);
    }

    [Fact]
    public void Detect_DatedRecordRows_GetPanel()
    {
        var text = string.Join('\n',
            "Посев на микрофлору",
            "Дата | Показатель | Результат | Норма",
            "09.06.2026 13:51 | Бактериальные микроорганизмы | 10^4 | < 10^3");

        LabTableRowDetector.Detect(text).Rows.Single().PanelLabel.Should().Be("Посев на микрофлору");
    }

    [Fact]
    public void Detect_DatedProtocol_SectionTitleIsPanel_ColumnFragmentsAndDocTitleAreNot()
    {
        // "Протокол лабораторного исследования" (название документа) и "Референтный" (обрывок шапки
        // колонок) — не разделы; "Отдельные лабораторные тесты" — раздел.
        var result = LabTableRowDetector.Detect(DatedProtocol);

        result.Rows.Should().NotBeEmpty();
        result.Rows.Select(r => r.PanelLabel).Should().AllBe("Отдельные лабораторные тесты");
    }
}
