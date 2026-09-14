using System.Text.RegularExpressions;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>Смысл качественного лабораторного результата — "найдено/есть" или "не найдено/нет",
/// независимо от рода/числа/формулировки ("обнаружена"/"обнаружены"/"положительно" — всё одно и то
/// же Positive; "не обнаружено"/"отсутствуют"/"отрицательно" — всё одно и то же Negative). Раньше
/// сравнение шло голым равенством строк (см. IndicatorFlagCalculator) — "не обнаружены" не
/// совпадало с нормой "не обнаружено" ни числом, ни родом, и результат молча уезжал в Unknown, хотя
/// смысл совпадает.</summary>
public enum QualitativePolarity
{
    Unknown,
    NegativeFinding,
    PositiveFinding,
}

/// <summary>Классификатор по корню слова + частице отрицания — не словарь словоформ (род/число не
/// имеют значения сами по себе), поэтому не нужно перечислять "обнаружена"/"обнаружены"/
/// "обнаружено" отдельно. "Не"/"not" перед корнем ИНВЕРТИРУЕТ его полярность, поэтому "не
/// отсутствуют" (двойное отрицание, встречается на реальных бланках) распознаётся как Positive
/// корректно, без отдельного правила.</summary>
public static class QualitativeResultClassifier
{
    private static readonly string[] PositiveRoots = ["обнаруж", "выявл", "присутств", "положит", "detect", "positive"];
    private static readonly string[] NegativeRoots = ["отсутств", "отрицат", "negative", "absent"];

    // Короткие аббревиатуры сравниваем целым словом, не StartsWith — иначе "позиция"/"негодный"
    // ложно совпали бы с "pos"/"neg".
    private static readonly HashSet<string> PositiveWords = new(StringComparer.Ordinal) { "pos" };
    private static readonly HashSet<string> NegativeWords = new(StringComparer.Ordinal) { "neg" };

    private static readonly Regex Punctuation = new(@"[^\p{L}0-9\s]", RegexOptions.Compiled);

    public static QualitativePolarity Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return QualitativePolarity.Unknown;

        var words = Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            // "не" слитно с корнем ("необнаружено" — артефакт OCR без пробела) — та же инверсия,
            // что и раздельным словом ниже. Длина > 2 — само слово "не" не должно резать себя.
            var negatedByPrefix = word.Length > 2 && word.StartsWith("не", StringComparison.Ordinal);
            var bare = negatedByPrefix ? word[2..] : word;

            var polarity = MatchRoot(bare);
            if (polarity is null && negatedByPrefix)
            {
                // Ни "не"+корень, ни само слово не подошли ("неделя", "неготово" и т.п.) — не
                // качественный результат, идём дальше по тексту.
                continue;
            }
            if (polarity is null) continue;

            var negatedBySeparateWord = !negatedByPrefix && i > 0 && (words[i - 1] == "не" || words[i - 1] == "not");
            return negatedByPrefix || negatedBySeparateWord ? Invert(polarity.Value) : polarity.Value;
        }

        return QualitativePolarity.Unknown;
    }

    private static QualitativePolarity? MatchRoot(string word)
    {
        if (PositiveWords.Contains(word)) return QualitativePolarity.PositiveFinding;
        if (NegativeWords.Contains(word)) return QualitativePolarity.NegativeFinding;
        foreach (var root in PositiveRoots)
            if (word.StartsWith(root, StringComparison.Ordinal)) return QualitativePolarity.PositiveFinding;
        foreach (var root in NegativeRoots)
            if (word.StartsWith(root, StringComparison.Ordinal)) return QualitativePolarity.NegativeFinding;
        return null;
    }

    private static QualitativePolarity Invert(QualitativePolarity polarity) => polarity switch
    {
        QualitativePolarity.PositiveFinding => QualitativePolarity.NegativeFinding,
        QualitativePolarity.NegativeFinding => QualitativePolarity.PositiveFinding,
        _ => polarity,
    };

    private static string Normalize(string text) =>
        Punctuation.Replace(text.Trim().ToLowerInvariant().Replace('ё', 'е'), " ");
}
