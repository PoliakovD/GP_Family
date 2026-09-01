using FamilyHub.Infrastructure.Enrichment;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Детерминированный merge референсных диапазонов по приоритету источника (ветка medicalrecords,
/// пересборка enrich-пайплайна анализов) — чистая функция, без LLM и без БД. Модель
/// (LabAnalyteKbSummarizer) возвращает "сырые" диапазоны, каждый со своим SourceIndex (индекс
/// сниппета, из которого он взят); приоритет источника при конфликте решает КОД, а не модель — тот
/// же принцип, что и антигаллюцинационный гейт: доверять модели можно в извлечении факта, но не в
/// сравнении надёжности источников. Сниппеты должны быть переданы в ТОМ ЖЕ порядке, что и модели
/// (уже отсортированы по приоритету домена — см. LabAnalyteEnrichmentProcessor), но приоритет здесь
/// вычисляется заново по домену, не по позиции в списке, — устойчиво к тому, что модель могла
/// процитировать источники не по порядку.
/// </summary>
public static class ReferenceRangeMerger
{
    /// <summary>Группировка конфликтов по (тип нормы, категория популяции, детали популяции, пол,
    /// возрастные границы) — внутри группы побеждает диапазон с минимальным SourceRank (0 —
    /// самый приоритетный домен из trustedDomainsByPriority). Группы, которых нет у топ-источника,
    /// добираются из следующих по рангу — GroupBy сохраняет их независимо от того, какой источник
    /// внёс группу, поэтому дополнительной логики "добора" не требуется.</summary>
    public static List<LabAnalyteReferenceRange> Merge(
        IReadOnlyList<LabAnalyteReferenceRange> rawRanges,
        IReadOnlyList<WebSnippet> snippets,
        IReadOnlyList<string> trustedDomainsByPriority)
    {
        if (rawRanges.Count == 0) return [];

        var withSource = rawRanges.Select(r =>
        {
            var domain = ResolveDomain(r.SourceIndex, snippets);
            return r with { SourceDomain = domain, SourceRank = ResolveRank(domain, trustedDomainsByPriority) };
        });

        return withSource
            .GroupBy(r => (r.NormKind, r.Population, r.PopulationDetail, r.Sex, r.AgeFrom, r.AgeTo))
            .Select(group => group.OrderBy(r => r.SourceRank).First())
            .OrderBy(r => r.SourceRank)
            .ThenBy(r => r.AgeFrom ?? -1)
            .ToList();
    }

    private static string? ResolveDomain(int? sourceIndex, IReadOnlyList<WebSnippet> snippets)
    {
        if (sourceIndex is null || sourceIndex < 0 || sourceIndex >= snippets.Count) return null;
        return Uri.TryCreate(snippets[sourceIndex.Value].Url, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    /// <summary>Индекс домена в trustedDomainsByPriority (меньше — приоритетнее, точное совпадение
    /// или поддомен, как в EnrichmentSnippetFilter.IsTrustedDomain); неопознанный/отсутствующий домен —
    /// ниже всех известных, не выше.</summary>
    private static int ResolveRank(string? domain, IReadOnlyList<string> trustedDomainsByPriority)
    {
        if (domain is null) return trustedDomainsByPriority.Count;

        for (var i = 0; i < trustedDomainsByPriority.Count; i++)
        {
            var trusted = trustedDomainsByPriority[i];
            if (domain.Equals(trusted, StringComparison.OrdinalIgnoreCase) ||
                domain.EndsWith("." + trusted, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return trustedDomainsByPriority.Count;
    }
}
