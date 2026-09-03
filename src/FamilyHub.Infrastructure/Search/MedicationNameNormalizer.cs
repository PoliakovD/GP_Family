using System.Text.RegularExpressions;

namespace FamilyHub.Infrastructure.Search;

/// <summary>
/// Приводит распознанное/введённое название препарата к ключу дедупликации справочника
/// (<c>GlobalMedicationKb.NormalizedName</c>, этап 4): "Парацетамол 400мг таб. №20" →
/// "парацетамол". Без этого один и тот же препарат превращался бы в десятки разных строк
/// справочника (фасовка/дозировка/форма выпуска у одной и той же упаковки различаются от
/// экземпляра к экземпляру, OCR добавляет к этому ещё и опечатки/путаницу раскладки).
/// Чистая функция, без состояния — безопасно как singleton.
/// </summary>
public static partial class MedicationNameNormalizer
{
    /// <summary>Дозировка/фасовка с единицей измерения: "400мг", "0.5 г", "10мл", "500 IU".</summary>
    [GeneratedRegex(@"\d+(?:[.,]\d+)?\s*(?:мг|мкг|г|мл|л|ме|iu|%)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DosageRegex();

    /// <summary>Номер серии/упаковки: "№20", "N 20".</summary>
    [GeneratedRegex(@"(?:№|\bn)\s*\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex PackagingNumberRegex();

    /// <summary>Количество единиц с сокращённой формой выпуска: "20 таб", "10шт".</summary>
    [GeneratedRegex(@"\d+\s*(?:таб|табл|капс|шт|доз|амп)\.?\b", RegexOptions.IgnoreCase)]
    private static partial Regex UnitCountRegex();

    /// <summary>Те же сокращения формы выпуска сами по себе, без числа: "таб.", "капс.".</summary>
    [GeneratedRegex(@"\b(?:таб|табл|капс|шт|доз|амп)\.?\b", RegexOptions.IgnoreCase)]
    private static partial Regex UnitAbbreviationRegex();

    /// <summary>Полные слова формы выпуска по префиксу — любое словоизменение/падеж.</summary>
    [GeneratedRegex(
        @"\b(?:таблет\w*|капсул\w*|сироп\w*|маз[ьи]\w*|капл\w*|раствор\w*|суспенз\w*|спрей\w*|ампул\w*|свеч\w*|гел\w*|крем\w*|порош\w*)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex FormWordRegex();

    [GeneratedRegex(@"покрыт\w*\s+оболочк\w*", RegexOptions.IgnoreCase)]
    private static partial Regex CoatedTabletRegex();

    [GeneratedRegex(@"пролонг\w*", RegexOptions.IgnoreCase)]
    private static partial Regex ProlongedActionRegex();

    /// <summary>Всё, что не буква/цифра/пробел — пунктуация, скобки, точки после сокращений.</summary>
    [GeneratedRegex(@"[^\p{L}\p{Nd}\s]")]
    private static partial Regex PunctuationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// "Парацетамол 400мг таб. №20" → "парацетамол". Пустая строка на входе даёт пустую строку
    /// на выходе (вызывающий код сам решает, что делать с пустым результатом — здесь не бросаем).
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        // Снять эхо-индекс/нумерацию пункта списка ДО починки гомоглифов/регистра — та же дыра,
        // что была у LabAnalyteNormalizer до пересборки enrich-пайплайна: без этого шага
        // "1. Парацетамол" и "Парацетамол" расходились в разные ключи дедупликации (числовая
        // нумерация из OcrNameCorrector.BuildUserText/эхо модели пунктуацией ниже не снимается —
        // цифра не пунктуация).
        var withoutMarkers = LabTextCleanupHelpers.StripLeadingMarkers(raw);
        var fixedScript = LabTextCleanupHelpers.FixMixedScriptHomoglyphs(withoutMarkers);
        var lower = fixedScript.ToLowerInvariant().Replace('ё', 'е');

        var stripped = DosageRegex().Replace(lower, " ");
        stripped = PackagingNumberRegex().Replace(stripped, " ");
        stripped = UnitCountRegex().Replace(stripped, " ");
        stripped = UnitAbbreviationRegex().Replace(stripped, " ");
        stripped = CoatedTabletRegex().Replace(stripped, " ");
        stripped = ProlongedActionRegex().Replace(stripped, " ");
        stripped = FormWordRegex().Replace(stripped, " ");

        var noPunctuation = PunctuationRegex().Replace(stripped, " ");
        return WhitespaceRegex().Replace(noPunctuation, " ").Trim();
    }
}
