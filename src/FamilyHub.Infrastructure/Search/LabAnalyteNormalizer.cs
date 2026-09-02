using System.Text.RegularExpressions;

namespace FamilyHub.Infrastructure.Search;

/// <summary>
/// Приводит распознанное название показателя анализа к ключу дедупликации
/// (<c>LabIndicator.AnalyteKey</c> / <c>GlobalLabAnalyteKb.NormalizedName</c>, ветка
/// medicalrecords): "1. Гемоглобин (HGB), г/л" → "гемоглобин". Без этого один и тот же показатель
/// у разных лабораторий (разные сокращения, единицы, порядок слов в бланке, нумерация пункта)
/// превращался бы в отдельные строки справочника и разрывал бы тренд по показателю. Сокращение в
/// скобках (аббревиатура вроде "HGB") сюда не включается — оно уходит в KB как алиас (см.
/// LabAnalyteEnrichmentProcessor/LabAnalyteKbWriter), не в сам ключ. Не путать с
/// <see cref="LabAnalyteNameCleaner.Clean"/> — тот даёт текст ДЛЯ ЧЕЛОВЕКА (сохраняет скобки,
/// единицы, регистр аббревиатур), этот — ключ для сравнения (режет всё, что мешает совпадению).
/// Чистая функция, без состояния — безопасно как singleton, тот же приём, что
/// MedicationNameNormalizer.
/// </summary>
public static partial class LabAnalyteNormalizer
{
    /// <summary>Скобки с сокращением/кодом: "(HGB)", "(общий)" — убираются целиком вместе с
    /// содержимым, не только скобки.</summary>
    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex ParentheticalRegex();

    /// <summary>Единицы измерения, которыми часто заканчивается название в бланке: "г/л",
    /// "ммоль/л", "мкмоль/л", "Ед/л", "%".</summary>
    [GeneratedRegex(@",?\s*(?:г|мг|мкг|нг|моль|ммоль|мкмоль|ед|Ед|IU|МЕ)\s*/\s*(?:л|мл)\b|,?\s*%\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex UnitRegex();

    /// <summary>Экспоненциальная запись счёта клеток без словесной единицы: "×10^9/л", "x10 12/мл".</summary>
    [GeneratedRegex(@",?\s*[×x]\s*10\s*\^?\s*\d+\s*/\s*(?:л|мл)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CellCountRegex();

    [GeneratedRegex(@"[^\p{L}\p{Nd}\s]")]
    private static partial Regex PunctuationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        // Снять эхо-индекс/нумерацию пункта бланка ДО починки гомоглифов/регистра — иначе
        // "1. Гемоглобин" и "Гемоглобин" расходятся в разные ключи дедупликации (пересборка
        // enrich-пайплайна). PunctuationRegex ниже намеренно сохраняет цифры (\p{Nd}) — они
        // значимы внутри названия ("витамин B12", "17-ОН-прогестерон"), поэтому нумерацию нужно
        // снимать явным префиксным правилом, а не всей цифрой сразу.
        var withoutMarkers = LabTextCleanupHelpers.StripLeadingMarkers(raw);
        var fixedScript = LabTextCleanupHelpers.FixMixedScriptHomoglyphs(withoutMarkers);
        var lower = fixedScript.ToLowerInvariant().Replace('ё', 'е');

        var withoutParens = ParentheticalRegex().Replace(lower, " ");
        var withoutCellCount = CellCountRegex().Replace(withoutParens, " ");
        var withoutUnits = UnitRegex().Replace(withoutCellCount, " ");
        var noPunctuation = PunctuationRegex().Replace(withoutUnits, " ");
        return WhitespaceRegex().Replace(noPunctuation, " ").Trim();
    }
}
