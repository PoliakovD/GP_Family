namespace FamilyHub.Infrastructure.Search;

/// <summary>
/// Компактный порт русского стеммера алгоритма Snowball (тот же алгоритм лежит в основе
/// text-search конфигурации Postgres <c>russian</c> — используем его и здесь, чтобы поведение
/// in-memory поиска совпадало с Postgres-FTS, см. ADR-0003). Оперирует регионами RV/R1/R2 (как в
/// Porter2): последовательно снимает перфективное деепричастие/возвратный постфикс/окончание
/// прилагательного-причастия-глагола-существительного, затем "и", затем деривационное окончание
/// в R2, затем упрощает "нн"→"н" и снимает финальный мягкий знак/превосходную степень. Без внешних
/// зависимостей (без Lucene.NET).
/// </summary>
internal static class RussianStemmer
{
    private const string Vowels = "аеиоуыэюя";

    private static readonly string[] PerfectiveGerund1 = ["в", "вши", "вшись"];
    private static readonly string[] PerfectiveGerund2 = ["ив", "ивши", "ившись", "ыв", "ывши", "ывшись"];
    private static readonly string[] Reflexive = ["ся", "сь"];
    private static readonly string[] Adjective =
    [
        "ими", "ыми", "его", "ого", "ему", "ому", "ими", "ых", "их", "ую", "юю", "ая", "яя", "ою", "ею",
        "ее", "ие", "ые", "ое", "ей", "ий", "ый", "ой", "ем", "им", "ым", "ом",
    ];
    private static readonly string[] Participle1 = ["ем", "нн", "вш", "ющ", "щ"];
    private static readonly string[] Participle2 = ["ивш", "ывш", "ующ"];
    private static readonly string[] Verb1 =
    [
        "ла", "на", "ете", "йте", "ли", "й", "л", "ем", "н", "ло", "но", "ет", "ют", "ны", "ть", "ешь", "нно",
    ];
    private static readonly string[] Verb2 =
    [
        "ила", "ыла", "ена", "ейте", "уйте", "ите", "или", "ыли", "ей", "уй", "ил", "ыл", "им", "ым", "ен",
        "ило", "ыло", "ено", "ят", "ует", "уют", "ит", "ыт", "ены", "ить", "ыть", "ишь", "ую", "ю",
    ];
    private static readonly string[] Noun =
    [
        "иями", "ями", "ами", "иях", "иям", "ием", "ией", "ья", "ье", "ья",
        "ев", "ов", "ие", "ья", "ах", "ях", "ью", "ию", "ов",
        "а", "у", "ы", "и", "й", "е", "о", "я", "ю", "ь",
        "ов", "ев", "ей", "ой", "ий", "ям", "ем", "ам", "ом",
    ];
    private static readonly string[] Superlative = ["ейш", "ейше"];
    private static readonly string[] Derivational = ["ост", "ость"];

    /// <summary>Возвращает основу слова (нижний регистр). Пустая/короткая строка — без изменений.</summary>
    public static string Stem(string word)
    {
        if (string.IsNullOrEmpty(word) || word.Length <= 2)
            return word;

        var w = word;
        var rv = FindRegionStart(w, 0, requireVowelThenConsonant: false);
        var r1 = FindRegionStart(w, 0, requireVowelThenConsonant: true);
        var r2 = FindRegionStart(w, r1, requireVowelThenConsonant: true);

        // Шаг 1: перфективное деепричастие ИЛИ (возвратный + прилагательное/причастие/глагол/сущ.).
        if (!TryRemoveLongestInRegion(ref w, rv, PerfectiveGerund1, out _)
            && !TryRemoveLongestInRegion(ref w, rv, PerfectiveGerund2, out _))
        {
            var hadReflexive = TryRemoveLongestInRegion(ref w, rv, Reflexive, out _);

            if (!TryRemoveAdjectival(ref w, rv) && !TryRemoveVerb(ref w, rv) && !hadReflexive)
            {
                TryRemoveLongestInRegion(ref w, rv, Noun, out _);
            }
        }

        // Пересчитываем регионы — длина слова могла измениться.
        rv = FindRegionStart(w, 0, requireVowelThenConsonant: false);
        r1 = FindRegionStart(w, 0, requireVowelThenConsonant: true);
        r2 = FindRegionStart(w, r1, requireVowelThenConsonant: true);

        // Шаг 2: одиночная "и" в конце (в пределах RV).
        if (w.Length > rv && w.EndsWith('и') && w.Length - 1 >= rv)
            w = w[..^1];

        // Шаг 3: деривационное окончание в R2.
        TryRemoveLongestInRegion(ref w, r2, Derivational, out _);

        // Шаг 4: удвоенное "нн" → "н"; либо мягкий знак в конце (в RV); либо превосходная степень.
        rv = FindRegionStart(w, 0, requireVowelThenConsonant: false);
        if (w.EndsWith("нн"))
        {
            w = w[..^1];
        }
        else if (TryRemoveLongestInRegion(ref w, rv, Superlative, out _))
        {
            if (w.EndsWith("нн"))
                w = w[..^1];
        }
        else if (w.Length > rv && w.EndsWith('ь') && w.Length - 1 >= rv)
        {
            w = w[..^1];
        }

        return w.Length == 0 ? word : w;
    }

    private static bool TryRemoveAdjectival(ref string w, int rv)
    {
        if (!TryRemoveLongestInRegion(ref w, rv, Adjective, out _))
            return false;

        // Причастие — только в увеличенной степени соответствия (после снятого прилагательного).
        TryRemoveLongestInRegion(ref w, rv, Participle1, out _);
        TryRemoveLongestInRegion(ref w, rv, Participle2, out _);
        return true;
    }

    private static bool TryRemoveVerb(ref string w, int rv) =>
        TryRemoveLongestInRegion(ref w, rv, Verb1, out _) || TryRemoveLongestInRegion(ref w, rv, Verb2, out _);

    /// <summary>
    /// Находит начало региона: RV — сразу после первой гласной; R1/R2 — сразу после первого
    /// сочетания "гласная→согласная", начиная поиск с <paramref name="from"/>.
    /// </summary>
    private static int FindRegionStart(string w, int from, bool requireVowelThenConsonant)
    {
        if (!requireVowelThenConsonant)
        {
            for (var i = 0; i < w.Length; i++)
                if (Vowels.IndexOf(w[i]) >= 0)
                    return i + 1;
            return w.Length;
        }

        for (var i = Math.Max(from, 0); i < w.Length - 1; i++)
            if (Vowels.IndexOf(w[i]) >= 0 && Vowels.IndexOf(w[i + 1]) < 0)
                return i + 2;
        return w.Length;
    }

    /// <summary>Снимает самое длинное подходящее окончание из списка, если оно начинается не раньше <paramref name="regionStart"/>.</summary>
    private static bool TryRemoveLongestInRegion(ref string w, int regionStart, string[] suffixes, out string removed)
    {
        removed = string.Empty;
        string? best = null;
        foreach (var s in suffixes)
        {
            if (w.Length - s.Length < regionStart) continue;
            if (!w.EndsWith(s, StringComparison.Ordinal)) continue;
            if (best is null || s.Length > best.Length) best = s;
        }

        if (best is null) return false;
        w = w[..^best.Length];
        removed = best;
        return true;
    }
}
