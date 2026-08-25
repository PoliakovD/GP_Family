using System.Text;
using System.Text.RegularExpressions;

namespace FamilyHub.Infrastructure.Search;

/// <summary>
/// Приводит распознанное название показателя анализа к ключу дедупликации
/// (<c>LabIndicator.AnalyteKey</c> / <c>GlobalLabAnalyteKb.NormalizedName</c>, ветка
/// medicalrecords): "Гемоглобин (HGB), г/л" → "гемоглобин". Без этого один и тот же показатель
/// у разных лабораторий (разные сокращения, единицы, порядок слов в бланке) превращался бы в
/// отдельные строки справочника и разрывал бы тренд по показателю. Сокращение в скобках
/// (аббревиатура вроде "HGB") сюда не включается — оно уходит в KB как алиас (см.
/// LabAnalyteEnrichmentProcessor/LabAnalyteKbWriter), не в сам ключ. Чистая функция, без
/// состояния — безопасно как singleton, тот же приём, что MedicationNameNormalizer.
/// </summary>
public static partial class LabAnalyteNormalizer
{
    /// <summary>Латинские буквы, визуально неотличимые от кириллических — тот же артефакт OCR,
    /// что и у названий препаратов (см. MedicationNameNormalizer).</summary>
    private static readonly Dictionary<char, char> LatinToCyrillicHomoglyphs = new()
    {
        ['A'] = 'А', ['B'] = 'В', ['E'] = 'Е', ['K'] = 'К', ['M'] = 'М', ['H'] = 'Н',
        ['O'] = 'О', ['P'] = 'Р', ['C'] = 'С', ['T'] = 'Т', ['X'] = 'Х', ['Y'] = 'У',
        ['a'] = 'а', ['c'] = 'с', ['e'] = 'е', ['o'] = 'о', ['p'] = 'р', ['x'] = 'х', ['y'] = 'у',
    };

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

        var fixedScript = FixMixedScriptHomoglyphs(raw);
        var lower = fixedScript.ToLowerInvariant().Replace('ё', 'е');

        var withoutParens = ParentheticalRegex().Replace(lower, " ");
        var withoutCellCount = CellCountRegex().Replace(withoutParens, " ");
        var withoutUnits = UnitRegex().Replace(withoutCellCount, " ");
        var noPunctuation = PunctuationRegex().Replace(withoutUnits, " ");
        return WhitespaceRegex().Replace(noPunctuation, " ").Trim();
    }

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
