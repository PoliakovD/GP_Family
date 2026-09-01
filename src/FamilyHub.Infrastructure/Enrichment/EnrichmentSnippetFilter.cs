namespace FamilyHub.Infrastructure.Enrichment;

/// <summary>
/// Решает, какие сниппеты из уже полученных (свежих или закэшированных) реально доходят до
/// суммаризатора — пересборка enrich-пайплайна: провайдеры (BraveSearchProvider/YandexSearchProvider)
/// больше не отбрасывают недоверенные результаты сами, кэш хранит ВСЕ сниппеты. Правило: точечный
/// override конкретного URL (если задан администратором через кэш) побеждает; иначе решает
/// членство домена в текущем БД-списке доверенных доменов (EnrichmentTrustedDomain, топик-специфичный).
/// Чистая функция без БД — сам список доменов/overrides процессор достаёт заранее.
/// </summary>
public static class EnrichmentSnippetFilter
{
    /// <summary>Не капает по количеству — вызывающий код (LabAnalyteEnrichmentProcessor) может
    /// захотеть сначала отсортировать по приоритету домена, только потом Take(MaxSnippets).</summary>
    public static List<WebSnippet> SelectEnabled(
        IReadOnlyList<WebSnippet> snippets, IReadOnlyList<string> trustedDomains,
        IReadOnlyDictionary<string, bool>? overrides) =>
        snippets.Where(s => IsEnabled(s.Url, trustedDomains, overrides)).ToList();

    public static bool IsEnabled(string url, IReadOnlyList<string> trustedDomains, IReadOnlyDictionary<string, bool>? overrides) =>
        overrides is not null && overrides.TryGetValue(url, out var explicitFlag) ? explicitFlag : IsTrustedDomain(url, trustedDomains);

    /// <summary>Точное совпадение хоста или его поддомен ("www.vidal.ru" доверен, если доверен "vidal.ru") —
    /// перенесено из BraveSearchProvider/YandexSearchProvider без изменений в логике.</summary>
    public static bool IsTrustedDomain(string url, IReadOnlyList<string> trustedDomains)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        var host = uri.Host;
        return trustedDomains.Any(domain =>
            host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }
}
