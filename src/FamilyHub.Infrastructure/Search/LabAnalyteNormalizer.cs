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

    /// <summary>
    /// Приводит УЖЕ ГОТОВЫЙ ключ (строки справочника, задачи обогащения), посчитанный прежней версией
    /// <see cref="NormalizeAnalyteKey"/>, к текущей форме — кросс-алфавитная свёртка, которой у старых ключей ещё не
    /// было ("treponema pallidum" → тот же ключ, что даёт сегодня бланк). НЕ <see cref="NormalizeAnalyteKey"/>
    /// повторно: тот рассчитан на сырой текст бланка и снимает ведущее число как нумерацию пункта, а в ключе это
    /// часть названия ("17 он прогестерон" превратился бы в "он прогестерон"). Идемпотентно.
    /// </summary>
    public static string RenormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        return WhitespaceRegex().Replace(MedicalTextTransliterator.Fold(key), " ").Trim();
    }

    /// <summary>
    /// «Голова» ключа — сам аналит без LOINC-дескриптора свойства/системы: протоколы медорганизаций (ГБУЗ РК)
    /// печатают «Мочевина, молярная концентрация в сыворотке или плазме крови», и ключ
    /// "мочевина молярная концентрация в сыворотке или плазме крови" почти целиком состоит из общего для всей
    /// таблицы хвоста — триграммная схожесть с "билирубин прямой молярная концентрация в …" выше порога
    /// автопривязки (LabAnalyteKbLookupService), хотя показатели разные. Голова — то, что их различает
    /// ("мочевина"). Ключ без дескриптора возвращается как есть; ключ хранения (<see cref="NormalizeAnalyteKey"/>)
    /// не меняется — голова нужна только для сравнения. Вход — уже нормализованный ключ.
    /// </summary>
    public static string AnalyteHead(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        var match = LoincDescriptor.Match(key);
        return match.Success ? key[..match.Index].Trim() : key.Trim();
    }

    /// <summary>Есть ли в ключе LOINC-дескриптор (см. <see cref="AnalyteHead"/>).</summary>
    public static bool HasLoincDescriptor(string key) =>
        !string.IsNullOrWhiteSpace(key) && LoincDescriptor.IsMatch(key);

    /// <summary>Дескриптор «[массовая|молярная|каталитическая|…] концентрация в &lt;система&gt;» до конца ключа; перед
    /// ним обязано остаться хотя бы одно слово (lookbehind на букву/цифру) — иначе это не хвост, а всё название.
    /// Слова собраны через <see cref="MedicalTextTransliterator.Fold"/> — ключ уже свёрнут ("массовая" → "масовая",
    /// "объемная" → "обемная"), литералы паттерна обязаны быть в той же форме.</summary>
    private static readonly Regex LoincDescriptor = BuildLoincDescriptorRegex();

    private static Regex BuildLoincDescriptorRegex()
    {
        static string F(string word) => Regex.Escape(MedicalTextTransliterator.Fold(word));
        var properties = string.Join('|',
            new[] { "массовая", "молярная", "каталитическая", "объемная", "числовая", "арбитражная" }.Select(F));
        return new Regex(
            $@"(?<=[\p{{L}}\p{{Nd}}])\s+(?:(?:{properties})\s+)?{F("концентрация")}\s+{F("в")}\s+.+$",
            RegexOptions.Compiled);
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
