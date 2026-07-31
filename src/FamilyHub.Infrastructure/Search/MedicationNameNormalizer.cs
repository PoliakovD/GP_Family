using System.Text;
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
    /// <summary>
    /// Латинские буквы, визуально неотличимые от кириллических (частый артефакт OCR по русским
    /// упаковкам: модель распознаёт часть букв слова латиницей). Применяется ТОЛЬКО к словам, где
    /// уже есть хотя бы одна кириллическая буква — иначе честные латинские торговые названия
    /// ("Nurofen") превратились бы в кириллическую кашу.
    /// </summary>
    private static readonly Dictionary<char, char> LatinToCyrillicHomoglyphs = new()
    {
        ['A'] = 'А', ['B'] = 'В', ['E'] = 'Е', ['K'] = 'К', ['M'] = 'М', ['H'] = 'Н',
        ['O'] = 'О', ['P'] = 'Р', ['C'] = 'С', ['T'] = 'Т', ['X'] = 'Х', ['Y'] = 'У',
        ['a'] = 'а', ['c'] = 'с', ['e'] = 'е', ['o'] = 'о', ['p'] = 'р', ['x'] = 'х', ['y'] = 'у',
    };

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

        var fixedScript = FixMixedScriptHomoglyphs(raw);
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

    /// <summary>
    /// Заменяет латинские гомоглифы на кириллицу только внутри слов, где уже есть хотя бы одна
    /// кириллическая буква (иначе "Nurofen" превратился бы в нечитаемую смесь).
    /// </summary>
    private static string FixMixedScriptHomoglyphs(string input)
    {
        var words = input.Split(' ');
        for (var w = 0; w < words.Length; w++)
        {
            var word = words[w];
            if (!HasCyrillic(word) || !HasLatin(word)) continue;

            var sb = new StringBuilder(word.Length);
            foreach (var ch in word)
                sb.Append(LatinToCyrillicHomoglyphs.TryGetValue(ch, out var mapped) ? mapped : ch);

            words[w] = sb.ToString();
        }

        return string.Join(' ', words);
    }

    private static bool HasCyrillic(string s) => s.Any(ch => ch is >= 'Ѐ' and <= 'ӿ');

    private static bool HasLatin(string s) => s.Any(ch => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z');
}
