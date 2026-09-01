using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FamilyHub.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Enrichment;

/// <summary>
/// Типизированный HttpClient к Brave Search API (res/v1/web/search) — по образцу LmStudioJsonClient
/// (тот же приём: ловим только HttpRequestException/TaskCanceledException, наружу — пустой список,
/// без исключений). Один запрос на препарат (не по запросу на доверенный домен — экономия
/// free-tier квоты 2000/мес). Свободный тариф Brave не отдаёт extra_snippets — берём description
/// выдачи. Возвращает ВСЕ результаты выдачи как есть (пересборка enrich-пайплайна) — фильтрация по
/// доверенным доменам больше не здесь, а на процессоре (EnrichmentSnippetFilter, БД-список
/// EnrichmentTrustedDomain), чтобы админ мог поменять список без нового платного запроса.
/// </summary>
public class BraveSearchProvider(HttpClient httpClient, IOptions<EnrichmentOptions> options, ILogger<BraveSearchProvider> logger)
    : IMedicationSearchProvider
{
    public string Name => "Brave";

    public async Task<IReadOnlyList<WebSnippet>> SearchAsync(
        string normalizedName, WebSearchTopic topic = WebSearchTopic.Medication,
        SpecimenType specimen = SpecimenType.Unknown, CancellationToken ct = default)
    {
        var queryText = topic == WebSearchTopic.LabAnalyte
            ? SpecimenQueryLabel.BuildAnalyteSearchQuery(normalizedName, specimen)
            : $"{normalizedName} инструкция по применению";
        var query = Uri.EscapeDataString(queryText);
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

        var snippets = results
            .Where(r => !string.IsNullOrWhiteSpace(r.Url) && !string.IsNullOrWhiteSpace(r.Description))
            .Select(r => new WebSnippet(r.Title ?? string.Empty, r.Url!, r.Description!))
            .ToList();

        if (snippets.Count == 0)
        {
            logger.LogInformation("Brave Search по «{NormalizedName}»: пустая выдача", normalizedName);
        }

        return snippets;
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
