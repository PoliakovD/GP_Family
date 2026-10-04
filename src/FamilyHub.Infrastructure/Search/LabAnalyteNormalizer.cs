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

    /// <summary>
    /// Ключ дедупликации ПОКАЗАТЕЛЯ (<c>LabIndicator.AnalyteKey</c> / <c>GlobalLabAnalyteKb.NormalizedName</c>
    /// / <c>LabAnalyteSearchCache.NormalizedName</c>) — <see cref="Normalize"/> + кросс-алфавитная
    /// свёртка (<see cref="MedicalTextTransliterator.Fold"/>) финальным шагом, чтобы "Adenovirus" и
    /// "аденовирус" не расходились в разные строки справочника/тренда (живое прод-наблюдение: одна и
    /// та же лаборатория печатает показатель то латиницей, то кириллицей на разных бланках — без
    /// свёртки это два разных <c>AnalyteKey</c> и разорванный тренд, см. план "миграция AnalyteKey").
    ///
    /// НЕ используется для specimen-объектов (<c>GlobalSpecimenKb</c>, см.
    /// <c>UserSpecimenService</c>/<c>SpecimenResolver</c>/<c>GlobalSpecimenKbService</c> — те
    /// остаются на обычном <see cref="Normalize"/>) — специмины почти всегда однословные русские
    /// термины, а бэкофилла для их справочника (аналога <c>LabAnalyteKbRebuildJob</c>) не
    /// существует; свернуть их ключ значило бы открыть миграцию без механизма её закрыть.
    ///
    /// Абсолютная форма показателя ("Нейтрофилы (абс.)", "NEU#") отличается от процентной ("Нейтрофилы,
    /// %", "NEU%") только тем, что Normalize вырезает (скобки, "#", завершающий "%"), — без этого шага обе
    /// давали один ключ: на одном бланке лейкоцитарной формулы вторая строка молча перезаписывала первую
    /// (уникальный индекс записи), а абсолютное значение получало процентные нормы справочника. Поэтому
    /// маркер абсолютной формы, потерянный при нормализации, возвращается в ключ словом "абс" — тем же,
    /// что уже даёт бланк, печатающий "Нейтрофилы, абс." вне скобок (тренды сходятся). Процентная форма
    /// ключ НЕ меняет — существующие строки справочника и кэша поиска остаются валидными. Идемпотентно:
    /// ключ, уже содержащий "абс", повторно не дополняется.
    /// </summary>
    public static string NormalizeAnalyteKey(string? raw)
    {
        var normalized = Normalize(raw);
        if (normalized.Length > 0 && AbsoluteMarkerRegex().IsMatch(raw!) && !AbsoluteWordRegex().IsMatch(normalized))
            normalized += " " + AbsoluteKeyWord;
        return MedicalTextTransliterator.Fold(normalized);
    }

    /// <summary>Слово-маркер абсолютной формы в ключе показателя.</summary>
    public const string AbsoluteKeyWord = "абс";

    /// <summary>Маркер абсолютной формы в сыром названии: "абс"/"abs" отдельным словом (в т.ч. в скобках —
    /// "(абс.)", "(абс. кол-во)"), "абсолютн…", "#" ("NEU#").</summary>
    [GeneratedRegex(@"(?<!\p{L})(?:абс|abs)(?!\p{L})|абсолютн|#", RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteMarkerRegex();

    /// <summary>Маркер уже есть в нормализованном ключе (слово, начинающееся с "абс"/"abs": "абс",
    /// "абсолютное") — дописывать не нужно.</summary>
    [GeneratedRegex(@"(?<!\p{L})(?:абс|abs)", RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteWordRegex();
}
