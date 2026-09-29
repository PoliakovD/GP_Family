using System.Text.RegularExpressions;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>Одна найденная строка-кандидат таблицы показателей — уже разбита на ячейки
/// (LayoutTextReconstructor вставляет "|" на границах колонок), но НЕ распарсена в
/// имя/значение/единицу/референс: это по-прежнему делает LLM (см. LmStudioMedicalDocumentExtractor),
/// только теперь на строго определённом наборе строк с явным id вместо целого чанка текста —
/// детектор отвечает только на вопрос "это результат анализа или нет".</summary>
public sealed record LabTableRow(string RowId, IReadOnlyList<string> Cells, string RawLine);

/// <summary>Результат разбора одной страницы/документа: только строки-результаты идут дальше в
/// LLM, PanelHeaderLines/NoiseLines — для диагностики (почему модель получила меньше строк, чем
/// казалось при взгляде на бланк) и для решения "нужен ли fallback-проход
/// analysis.enumerate-rows" (Rows.Count == 0 на непустом документе — форма нестандартная,
/// эвристика не распознала ни одной строки).</summary>
public sealed record LabTableDetectionResult(
    IReadOnlyList<LabTableRow> Rows, int PanelHeaderLines, int NoiseLines);

/// <summary>
/// Детерминированный (без LLM) детектор строк-результатов в тексте бланка анализов,
/// реконструированном LayoutTextReconstructor. Решает задачу, которую раньше решала (плохо) одна
/// большая LLM-подсказка на весь чанк текста: маленькая модель (см. план "качество
/// ИИ-распознавания анализов") должна получать НЕ "вот текст, найди в нём всё, что относится к
/// анализам", а точный список пронумерованных строк-кандидатов ("[R5] Инсулин | ↓ 1.2 | мкЕд/мл |
/// 2.2 - 25") — тогда её ответ можно СВЕРИТЬ по покрытию (все ли Rows получили результат) и
/// повторить только пропущенные, вместо того чтобы гадать, честно ли модель прочитала весь текст
/// или тихо что-то упустила.
///
/// Правила ниже откалиброваны на реальном бланке лаборатории Гемотест (13 показателей на 3
/// страницах: единичные показатели, панели вида "HOMA-IR"/"МНО (+ПТВ и ПТИ)" с несколькими
/// вложенными строками, цензурированные значения "&lt;0.9", повторяющаяся на каждой странице
/// шапка таблицы и техническая "вода" — коды пункта номенклатуры, "Метод:"/"Оборудование:",
/// сведения о пробе) — точный набор эвристик стоит донастраивать по мере появления новых форм
/// через eval-стенд (план, Этап 0), а не переписывать вслепую.
/// </summary>
public static class LabTableRowDetector
{
    /// <summary>Строка считается строкой-результатом, только если её первая ячейка (имя
    /// показателя) короче этого предела — тот же порог, что и в LmStudioMedicalDocumentExtractor
    /// для итогового имени показателя (MaxIndicatorNameLength), чтобы детектор не пропускал то,
    /// что дальше по конвейеру всё равно будет принято.</summary>
    private const int MaxNameLength = 160;

    /// <summary>Ячейка-значение из чисто буквенной "качественной" фразы (например
    /// "не обнаружено", "отрицательно") должна быть хотя бы такой длины — однобуквенные ячейки
    /// вроде "Ж"/"М" (пол пациента в шапке документа, НЕ результат анализа) не должны случайно
    /// сойти за качественный результат.</summary>
    private const int MinQualitativeValueLength = 2;

    private const int MaxQualitativeValueLength = 30;

    /// <summary>Ячейка-значение: необязательная стрелка тренда (↑/↓, печатается лабораториями
    /// рядом со значением вне нормы) + одно из трёх: цензурированное неравенство ("&lt;0.9",
    /// "&gt; 47"), обычное число ("4.41", "-3", "90"), либо короткая качественная фраза
    /// ("отрицательно", "не обнаружено", "единичные") — буквы/пробелы/дефис/слэш, без цифр (даты и
    /// технические коды на цифры и опираются, см. NoiseLinePrefixes/длина). Квантификатор длины
    /// качественной фразы собран из Min/MaxQualitativeValueLength (минус 1, т.к. первая буква
    /// вынесена в отдельный класс символов вне квантификатора) — держать эти числа в двух местах
    /// синхронизированными опаснее, чем собрать паттерн строкой один раз в статике.</summary>
    private static readonly Regex ValueCellPattern = new(
        "^(?:[↑↓]\\s*)?(?:[<>≤≥]\\s?\\d+(?:[.,]\\d+)?|-?\\d+(?:[.,]\\d+)?|[А-ЯЁа-яё][А-ЯЁа-яё\\s/-]{" +
        (MinQualitativeValueLength - 1) + "," + (MaxQualitativeValueLength - 1) + "})$",
        RegexOptions.Compiled);

    /// <summary>Строки, которые НИКОГДА не являются результатом анализа, даже если случайно прошли
    /// бы по форме "имя | что-то похожее на значение" — техническая "вода" бланка: сведения о
    /// пробе/заборе материала, методика и оборудование, дата исследования отдельной строкой, код
    /// пункта номенклатуры ("1.50.", "6.10.", "26.2." — три эталонных примера с бланка Гемотест),
    /// сноска мелким шрифтом внизу таблицы.</summary>
    private static readonly Regex[] NoiseLinePrefixes =
    [
        new(@"^Проба\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^Метод\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^Оборудование\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^Дата\s+исследования\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\d+\.\d+\.?\s*$", RegexOptions.Compiled),
        new(@"^\*", RegexOptions.Compiled),
    ];

    /// <summary>Синонимы заголовков колонок таблицы результатов — по ним детектор понимает, что
    /// строка целиком является шапкой таблицы (не результатом) и, что важнее, отмечает начало
    /// "табличной зоны" документа (см. Detect: inTable). До первой такой строки ничего не
    /// засчитывается результатом, даже если формально похоже — это и есть защита от главной
    /// путаницы бланков ("Тестовна Теста Тестовична | Ж | 22.02.2002" в шапке документа выглядит
    /// ровно как "Имя | короткое_значение", но стоит ДО таблицы результатов).</summary>
    private static readonly HashSet<string> HeaderNameSynonyms = new(StringComparer.OrdinalIgnoreCase)
        { "исследование", "показатель", "наименование", "наименование исследования" };

    private static readonly HashSet<string> HeaderValueSynonyms = new(StringComparer.OrdinalIgnoreCase)
        { "результат", "значение" };

    private static readonly HashSet<string> HeaderUnitSynonyms = new(StringComparer.OrdinalIgnoreCase)
        { "ед. изм.", "ед.изм.", "единицы", "единица измерения" };

    private static readonly HashSet<string> HeaderRefSynonyms = new(StringComparer.OrdinalIgnoreCase)
        { "реф. значения", "референсные значения", "референс", "норма" };

    public static LabTableDetectionResult Detect(string reconstructedText)
    {
        var rows = new List<LabTableRow>();
        var panelHeaderLines = 0;
        var noiseLines = 0;
        var inTable = false;

        foreach (var rawLine in reconstructedText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("--- стр.", StringComparison.Ordinal)) continue;

            var cells = line.Split(" | ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (cells.Length == 0) continue;

            if (IsHeaderRow(cells))
            {
                inTable = true;
                noiseLines++;
                continue;
            }

            if (NoiseLinePrefixes.Any(pattern => pattern.IsMatch(cells[0])))
            {
                noiseLines++;
                continue;
            }

            if (inTable && cells.Length is >= 2 and <= 4 &&
                cells[0].Length is > 0 and <= MaxNameLength && ValueCellPattern.IsMatch(cells[1]))
            {
                rows.Add(new LabTableRow($"R{rows.Count + 1}", cells, line));
                continue;
            }

            if (cells.Length == 1)
            {
                panelHeaderLines++;
                continue;
            }

            noiseLines++;
        }

        return new LabTableDetectionResult(rows, panelHeaderLines, noiseLines);
    }

    private static bool IsHeaderRow(IReadOnlyList<string> cells)
    {
        var matchedConcepts = 0;
        if (cells.Any(c => HeaderNameSynonyms.Contains(c))) matchedConcepts++;
        if (cells.Any(c => HeaderValueSynonyms.Contains(c))) matchedConcepts++;
        if (cells.Any(c => HeaderUnitSynonyms.Contains(c))) matchedConcepts++;
        if (cells.Any(c => HeaderRefSynonyms.Contains(c))) matchedConcepts++;
        return matchedConcepts >= 2;
    }
}
