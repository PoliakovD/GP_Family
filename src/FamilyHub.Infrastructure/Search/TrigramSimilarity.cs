namespace FamilyHub.Infrastructure.Search;

/// <summary>
/// Символьные триграммы + мера схожести Жаккара — та же модель, что использует Postgres-расширение
/// <c>pg_trgm</c> (функция <c>similarity()</c>: доля общих триграмм от их объединения), см. ADR-0003.
/// Устойчивость к опечаткам OCR: два близких по написанию слова делят большинство триграмм, даже
/// если отличаются на 1-2 символа. Используется как fallback, когда морфологическое совпадение
/// (см. <see cref="RussianStemmer"/>) не найдено — и как сторонняя проверка правдоподобия
/// "исправленного" названия препарата (см. MedicationEnrichmentProcessor в Modules.Medical),
/// поэтому публичный, а не internal.
/// </summary>
public static class TrigramSimilarity
{
    /// <summary>Схожесть двух строк по Жаккару на множестве символьных триграмм, [0..1].</summary>
    public static double Similarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        if (a == b) return 1;

        var setA = Trigrams(a);
        var setB = Trigrams(b);
        if (setA.Count == 0 || setB.Count == 0) return 0;

        var intersection = 0;
        var smaller = setA.Count <= setB.Count ? setA : setB;
        var larger = setA.Count <= setB.Count ? setB : setA;
        foreach (var t in smaller)
            if (larger.Contains(t))
                intersection++;

        var union = setA.Count + setB.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    /// <summary>
    /// Триграммы с граничными пробелами (как в pg_trgm: слово дополняется пробелами по краям,
    /// поэтому короткие слова и начало/конец слова тоже участвуют в сравнении).
    /// </summary>
    private static HashSet<string> Trigrams(string word)
    {
        var padded = "  " + word + " ";
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i <= padded.Length - 3; i++)
            result.Add(padded.Substring(i, 3));
        return result;
    }
}
