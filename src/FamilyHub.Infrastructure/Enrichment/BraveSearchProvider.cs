using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Prompts;
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
/// Текст запроса для медикаментов (в отличие от LabAnalyte, см. AnalyteSearchQueryBuilder)
/// редактируется из админки отдельным ключом "medication.search-query.brave" (см. class doc
/// IPromptProvider) — сформулирован под Brave как обычный ключевой поиск, у Yandex своя
/// формулировка под GenSearch (см. class doc YandexSearchProvider).
/// </summary>
public class BraveSearchProvider(
    HttpClient httpClient, IOptions<EnrichmentOptions> options, ILogger<BraveSearchProvider> logger,
    AnalyteSearchQueryBuilder analyteQueryBuilder, IPromptProvider promptProvider, WebSearchCallLogger callLogger)
    : IMedicationSearchProvider
{
    public const string MedicationFallbackTemplate = "{name} инструкция по применению";

    public string Name => "Brave";

    public async Task<IReadOnlyList<WebSnippet>> SearchAsync(
        string normalizedName, WebSearchTopic topic = WebSearchTopic.Medication,
        string? specimenDisplayName = null, CancellationToken ct = default,
        WebSearchCallContext? callContext = null)
    {
        var queryText = topic == WebSearchTopic.LabAnalyte
            ? await analyteQueryBuilder.BuildAsync(normalizedName, specimenDisplayName, ct)
            : (await promptProvider.GetAsync("medication.search-query.brave", MedicationFallbackTemplate, ct))
                .Replace("{name}", normalizedName);
        var query = Uri.EscapeDataString(queryText);
        const string endpoint = "res/v1/web/search";
        var url = $"{endpoint}?q={query}&country=ru&search_lang=ru&ui_lang=ru&count=10";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        BraveSearchResponse? parsed;
        int? httpStatus = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Subscription-Token", options.Value.ApiKey);
            request.Headers.Add("Accept", "application/json");

            using var response = await httpClient.SendAsync(request, ct);
            httpStatus = (int)response.StatusCode;
            response.EnsureSuccessStatusCode();
            parsed = await response.Content.ReadFromJsonAsync<BraveSearchResponse>(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Brave Search недоступен или запрос по «{NormalizedName}» превысил таймаут", normalizedName);
            await LogCallAsync(
                normalizedName, topic, specimenDisplayName, queryText, endpoint, httpStatus, stopwatch.ElapsedMilliseconds,
                ex is TaskCanceledException ? WebSearchCallOutcome.Timeout : WebSearchCallOutcome.HttpError,
                [], ex.Message, callContext, ct);
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

        await LogCallAsync(
            normalizedName, topic, specimenDisplayName, queryText, endpoint, httpStatus, stopwatch.ElapsedMilliseconds,
            snippets.Count > 0 ? WebSearchCallOutcome.Ok : WebSearchCallOutcome.Empty,
            snippets.Select(s => s.Url).ToList(), null, callContext, ct);

        return snippets;
    }

    private Task LogCallAsync(
        string normalizedName, WebSearchTopic topic, string? specimenDisplayName, string queryText, string endpoint,
        int? httpStatus, long durationMs, WebSearchCallOutcome outcome, IReadOnlyList<string> resultUrls, string? error,
        WebSearchCallContext? callContext, CancellationToken ct) =>
        callLogger.LogAsync(new WebSearchCallLogEntry(
            Name, topic, normalizedName, specimenDisplayName, queryText, endpoint, httpStatus, (int)durationMs,
            outcome, resultUrls.Count, resultUrls, error, callContext?.JobKind, callContext?.JobId), ct);

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
