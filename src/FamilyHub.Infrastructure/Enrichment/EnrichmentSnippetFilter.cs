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
        snippets.Where(s => IsEnabled(s, trustedDomains, overrides)).ToList();

    /// <summary>Итоговый набор для суммаризатора: включённые сниппеты, ручные и закреплённые — первыми
    /// (админ добавил/закрепил их сознательно, лимит их не отрезает), остальные — в порядке выдачи или,
    /// при rankOrder, по приоритету домена (анализы: приоритетный источник не должен срезаться
    /// MaxSnippets, см. ReferenceRangeMerger). Порядок значим для индексов "[N]" в промпте.</summary>
    public static List<WebSnippet> SelectForSummary(
        IReadOnlyList<WebSnippet> snippets, IReadOnlyList<string> trustedDomains,
        IReadOnlyDictionary<string, bool>? overrides, int maxSnippets, bool rankOrder = false)
    {
        return snippets
            .Select((s, i) => (Snippet: s, Index: i))
            .Where(x => IsEnabled(x.Snippet, trustedDomains, overrides))
            .OrderBy(x => x.Snippet.Pinned || x.Snippet.Origin == SnippetOrigin.Manual ? 0 : 1)
            .ThenBy(x => !rankOrder ? 0
                : Uri.TryCreate(x.Snippet.Url, UriKind.Absolute, out var uri) ? RankOf(uri.Host, trustedDomains) : trustedDomains.Count)
            .ThenBy(x => x.Index)
            .Select(x => x.Snippet)
            .Take(maxSnippets)
            .ToList();
    }

    /// <summary>Как <see cref="IsEnabled(string, IReadOnlyList{string}, IReadOnlyDictionary{string, bool}?)"/>,
    /// но с учётом ручных и закреплённых сниппетов: они включены по умолчанию даже с недоверенного
    /// домена (недоверенный домен у ручного — предупреждение в UI, не блокировка); явный override
    /// администратора по-прежнему побеждает.</summary>
    public static bool IsEnabled(WebSnippet snippet, IReadOnlyList<string> trustedDomains, IReadOnlyDictionary<string, bool>? overrides) =>
        overrides is not null && overrides.TryGetValue(snippet.Url, out var explicitFlag)
            ? explicitFlag
            : snippet.Origin == SnippetOrigin.Manual || snippet.Pinned || IsTrustedDomain(snippet.Url, trustedDomains);

    public static bool IsEnabled(string url, IReadOnlyList<string> trustedDomains, IReadOnlyDictionary<string, bool>? overrides) =>
        overrides is not null && overrides.TryGetValue(url, out var explicitFlag) ? explicitFlag : IsTrustedDomain(url, trustedDomains);

    /// <summary>Точное совпадение хоста или его поддомен ("www.vidal.ru" доверен, если доверен "vidal.ru") —
    /// перенесено из BraveSearchProvider/YandexSearchProvider без изменений в логике.</summary>
    public static bool IsTrustedDomain(string url, IReadOnlyList<string> trustedDomains) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && RankOf(uri.Host, trustedDomains) < trustedDomains.Count;

    /// <summary>Индекс домена в trustedDomains по приоритету (меньше — приоритетнее; точное
    /// совпадение хоста или его поддомен, та же проверка, что <see cref="IsTrustedDomain"/>) —
    /// общий примитив ранжирования: раньше эту же проверку в цикле дублировали
    /// LabAnalyteEnrichmentProcessor.DomainRank (сортировка сниппетов перед лимитом на модель) и
    /// ReferenceRangeMerger.ResolveRank (приоритет источника при слиянии референсных диапазонов) —
    /// пересборка enrich-пайплайна, §5 плана. host — уже резолвленный (Uri.Host), не URL целиком:
    /// вызывающий код сам решает, откуда его взять (сырой URL или уже сохранённый SourceDomain).
    /// Неопознанный/отсутствующий домен — ниже всех известных приоритетов, не выше.</summary>
    public static int RankOf(string? host, IReadOnlyList<string> trustedDomains)
    {
        if (host is null) return trustedDomains.Count;

        for (var i = 0; i < trustedDomains.Count; i++)
        {
            var trusted = trustedDomains[i];
            if (host.Equals(trusted, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + trusted, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return trustedDomains.Count;
    }
}
