using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Kb;

namespace FamilyHub.Modules.Medical.Enrichment;

public enum MedicationNameCorrectionOutcome
{
    /// <summary>Модель не предлагала коррекцию (или она совпала с исходным именем).</summary>
    None,

    /// <summary>Коррекция принята — запись пишется под исправленным именем, исходное идёт алиасом.</summary>
    Corrected,

    /// <summary>Коррекция отклонена: схожесть ниже порога — похоже на другой препарат, не на опечатку.</summary>
    RejectedLowSimilarity,
}

public record MedicationNameResolution(
    string NormalizedName, string DisplayName, IReadOnlyList<string>? ExtraAliases,
    MedicationNameCorrectionOutcome Outcome, string? CorrectedName = null, double Similarity = 0);

/// <summary>
/// Разрешение «исправленного суммаризатором названия препарата» (OCR по упаковке искажает имя).
/// Вынесено из MedicationEnrichmentProcessor/VisitMedicationEnrichmentProcessor, где было
/// продублировано, когда то же решение понадобилось и ревью результата (ADR-0018:
/// «пересуммаризировать» должно разрешать имя ровно так же, как процессор).
/// </summary>
public static class MedicationNameCorrection
{
    /// <summary>Тот же порог, что pg_trgm.similarity_threshold (см. KbLookupService) — исправленное
    /// название должно быть очевидной опечаткой исходного, а не другим препаратом.</summary>
    public const double MinCorrectionSimilarity = 0.3;

    public static MedicationNameResolution Resolve(string jobNormalizedName, string jobDisplayName, MedicationSummary summary)
    {
        var unchanged = new MedicationNameResolution(
            jobNormalizedName, jobDisplayName, null, MedicationNameCorrectionOutcome.None);

        var correctedName = summary.CorrectedName?.Trim();
        if (string.IsNullOrEmpty(correctedName)) return unchanged;

        var correctedNormalized = MedicationNameNormalizer.Normalize(correctedName);
        if (correctedNormalized.Length == 0 || correctedNormalized == jobNormalizedName) return unchanged;

        var similarity = TrigramSimilarity.Similarity(correctedNormalized, jobNormalizedName);
        if (similarity < MinCorrectionSimilarity)
        {
            return unchanged with
            {
                Outcome = MedicationNameCorrectionOutcome.RejectedLowSimilarity,
                CorrectedName = correctedName,
                Similarity = similarity,
            };
        }

        return new MedicationNameResolution(
            correctedNormalized, correctedName, [jobNormalizedName],
            MedicationNameCorrectionOutcome.Corrected, correctedName, similarity);
    }

    /// <summary>"brave: vidal.ru, rlsnet.ru" — провайдер + реально использованные модельным ответом домены.</summary>
    public static string BuildSourceLabel(
        string providerName, IReadOnlyList<FamilyHub.Infrastructure.Enrichment.WebSnippet> snippets, IReadOnlyList<int> usedIndexes)
    {
        var domains = usedIndexes
            .Where(i => i >= 0 && i < snippets.Count)
            .Select(i => Uri.TryCreate(snippets[i].Url, UriKind.Absolute, out var uri) ? uri.Host : null)
            .Where(host => host is not null)
            .Distinct()
            .ToList();

        return domains.Count == 0 ? providerName : $"{providerName}: {string.Join(", ", domains)}";
    }
}
