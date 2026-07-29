using System.Text;

namespace FamilyHub.Infrastructure.Search;

/// <inheritdoc cref="IRussianTextSearcher"/>
public sealed class RussianTextSearcher : IRussianTextSearcher
{
    /// <summary>Тот же дефолт, что и <c>pg_trgm.similarity_threshold</c> в Postgres (0.3).</summary>
    private const double TrigramThreshold = 0.3;

    public double Score(string? text, string? query)
    {
        var queryTokens = Tokenize(query);
        var textTokens = Tokenize(text);
        if (queryTokens.Count == 0 || textTokens.Count == 0) return 0;

        var textStems = textTokens.Select(RussianStemmer.Stem).ToArray();

        var total = 0.0;
        foreach (var qToken in queryTokens)
        {
            var qStem = RussianStemmer.Stem(qToken);
            var best = 0.0;

            for (var i = 0; i < textTokens.Count; i++)
            {
                if (textStems[i] == qStem)
                {
                    best = 1;
                    break;
                }

                var sim = TrigramSimilarity.Similarity(textTokens[i], qToken);
                if (sim > best) best = sim;
            }

            // AND-семантика (как plainto_tsquery): слово запроса не нашлось — весь запрос мимо.
            if (best < TrigramThreshold) return 0;
            total += best;
        }

        return total / queryTokens.Count;
    }

    /// <summary>Нормализация (нижний регистр, ё→е) + разбиение на буквенно-цифровые токены.</summary>
    private static List<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var normalized = text.ToLowerInvariant().Replace('ё', 'е');
        var tokens = new List<string>();
        var current = new StringBuilder();

        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
