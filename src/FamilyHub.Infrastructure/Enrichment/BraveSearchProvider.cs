using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Enrichment;

/// <summary>
/// Типизированный HttpClient к Brave Search API (res/v1/web/search) — по образцу LmStudioJsonClient
/// (тот же приём: ловим только HttpRequestException/TaskCanceledException, наружу — пустой список,
/// без исключений). Один запрос на препарат (не по запросу на доверенный домен — экономия
/// free-tier квоты 2000/мес), фильтрация по TrustedDomains — уже на нашей стороне: конкретный
/// набор доверенных источников остаётся под нашим контролем, а не зависит от `site:`-синтаксиса
/// поисковика. Свободный тариф Brave не отдаёт extra_snippets — берём description выдачи.
/// </summary>
public class BraveSearchProvider(HttpClient httpClient, IOptions<EnrichmentOptions> options, ILogger<BraveSearchProvider> logger)
    : IMedicationSearchProvider
{
    public string Name => "Brave";

    public async Task<IReadOnlyList<WebSnippet>> SearchAsync(string normalizedName, CancellationToken ct = default)
    {
        var query = Uri.EscapeDataString($"{normalizedName} инструкция по применению");
        var url = $"res/v1/web/search?q={query}&country=ru&search_lang=ru&ui_lang=ru&count=10";

        BraveSearchResponse? parsed;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Subscription-Token", options.Value.ApiKey);
            request.Headers.Add("Accept", "application/json");

            using var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            parsed = await response.Content.ReadFromJsonAsync<BraveSearchResponse>(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Brave Search недоступен или запрос по «{NormalizedName}» превысил таймаут", normalizedName);
            return [];
        }

        var results = parsed?.Web?.Results ?? [];
        var trustedDomains = options.Value.TrustedDomains;

        var snippets = results
            .Where(r => !string.IsNullOrWhiteSpace(r.Url) && !string.IsNullOrWhiteSpace(r.Description))
            .Where(r => IsTrustedDomain(r.Url!, trustedDomains))
            .Select(r => new WebSnippet(r.Title ?? string.Empty, r.Url!, r.Description!))
            .Take(options.Value.MaxSnippets)
            .ToList();

        if (snippets.Count == 0)
        {
            logger.LogInformation(
                "Brave Search по «{NormalizedName}»: ни один результат не с доверенного домена", normalizedName);
        }

        return snippets;
    }

    /// <summary>Точное совпадение хоста или его поддомен ("www.vidal.ru" доверен, если доверен "vidal.ru").</summary>
    private static bool IsTrustedDomain(string url, IReadOnlyList<string> trustedDomains)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        var host = uri.Host;
        return trustedDomains.Any(domain =>
            host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }

    // --- DTO ответа Brave Search API (только нужные поля) ---

    private sealed record BraveSearchResponse(
        [property: JsonPropertyName("web")] BraveWebResults? Web);

    private sealed record BraveWebResults(
        [property: JsonPropertyName("results")] List<BraveResult>? Results);

    private sealed record BraveResult(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("description")] string? Description);
}
