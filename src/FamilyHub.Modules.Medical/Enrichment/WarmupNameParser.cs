using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Search;

namespace FamilyHub.Modules.Medical.Enrichment;

/// <summary>Одна строка из textarea прогрева — сырой текст (для SourceDisplayName/лога) и его
/// нормализованная форма (ключ кэша).</summary>
public record WarmupName(string Raw, string Normalized);

/// <summary>
/// Разбирает вставленный в textarea список названий (прогрев кэша веб-поиска, админка) в
/// уникальные по нормализованному ключу строки — чистая функция, без обращения к БД. Нормализация
/// той же функцией, что и сам конвейер обогащения: для Medication — MedicationNameNormalizer
/// (ключ GlobalMedicationKb.NormalizedName), для LabAnalyte — LabAnalyteNormalizer.NormalizeAnalyteKey
/// (свёрнутый ключ AnalyteKey/LabAnalyteSearchCache.NormalizedName — НЕ голый Normalize: без
/// кросс-алфавитной свёртки "Adenovirus"/"аденовирус" ушли бы двумя разными платными запросами).
/// </summary>
public static class WarmupNameParser
{
    private const int MaxLineLength = 200;

    public static IReadOnlyList<WarmupName> Parse(string? rawText, WebSearchTopic topic, int maxNames = 500)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return [];

        var result = new List<WarmupName>();
        var seen = new HashSet<string>();

        foreach (var line in rawText.Split('\n'))
        {
            var raw = line.Trim('\r', ' ', '\t').Trim();
            if (raw.Length == 0 || raw.Length > MaxLineLength) continue;

            var normalized = topic == WebSearchTopic.LabAnalyte
                ? LabAnalyteNormalizer.NormalizeAnalyteKey(raw)
                : MedicationNameNormalizer.Normalize(raw);
            if (normalized.Length == 0) continue;

            if (!seen.Add(normalized)) continue; // дубликат после нормализации — первая сырая форма побеждает

            result.Add(new WarmupName(raw, normalized));
            if (result.Count >= maxNames) break;
        }

        return result;
    }
}
