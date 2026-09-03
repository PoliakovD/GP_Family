using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Prompts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Enrichment;

/// <summary>
/// Yandex Web Search API v2/gen/search (GenSearch) — в отличие от Brave не возвращает сниппеты
/// на источник, а сразу генеративный ответ (message.content) + список источников с флагом used.
/// Два свойства подтверждены живым запросом к API (не только документацией):
/// 1) ответ приходит ОБЁРНУТЫМ В МАССИВ (gRPC-gateway streaming-стиль) даже без getPartialResults —
///    десериализуем List&lt;GenSearchResponse&gt;, берём последний элемент;
/// 2) жёсткое ограничение поиска через поле "host" (искать ТОЛЬКО по доверенным доменам) на
///    практике даёт "Ничего не найдено" — GenSearch триггерит несколько под-запросов и, видимо, не
///    находит источников внутри узкого host-набора для всех них разом, поэтому запрос самого поиска
///    этим полем не ограничивается.
/// Возвращает ВСЕ источники, которые Yandex реально использовал для ответа (used=true), независимо
/// от домена (пересборка enrich-пайплайна) — фильтрация по доверенным доменам ПОСТФАКТУМ переехала
/// на процессор (EnrichmentSnippetFilter, БД-список EnrichmentTrustedDomain), не здесь.
/// Суммаризация сгенерированного Yandex ответа всё равно проходит через локальный Qwen
/// (MedicationSummarizer) — ADR-0005 п.7: облачный сервис не пишет в справочник напрямую, только
/// поставляет сырой материал под тем же антигаллюцинационным гейтом, что и Brave.
/// Текст запроса для медикаментов редактируется из админки отдельным ключом
/// "medication.search-query.yandex" (см. class doc IPromptProvider) — сформулирован под GenSearch
/// (развёрнутый вопрос, а не ключевые слова), поэтому текст осознанно отличается от Brave-версии
/// (см. class doc BraveSearchProvider).
/// </summary>
public class YandexSearchProvider(
    HttpClient httpClient, IOptions<EnrichmentOptions> options, ILogger<YandexSearchProvider> logger,
    AnalyteSearchQueryBuilder analyteQueryBuilder, IPromptProvider promptProvider)
    : IMedicationSearchProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public const string MedicationFallbackTemplate =
        "{name}: инструкция по применению, показания, форма выпуска, условия хранения, влияние на управление транспортом";

    public string Name => "Yandex";

    public async Task<IReadOnlyList<WebSnippet>> SearchAsync(
        string normalizedName, WebSearchTopic topic = WebSearchTopic.Medication,
        string? specimenDisplayName = null, CancellationToken ct = default)
    {
        var opts = options.Value;
        var messageText = topic == WebSearchTopic.LabAnalyte
            ? await analyteQueryBuilder.BuildAsync(normalizedName, specimenDisplayName, ct)
            : (await promptProvider.GetAsync("medication.search-query.yandex", MedicationFallbackTemplate, ct))
                .Replace("{name}", normalizedName);
        var request = new GenSearchRequest(
            Messages: [new GenSearchMessage(messageText, "ROLE_USER")],
            FolderId: opts.FolderId ?? string.Empty,
            FixMisspell: true,
            SearchType: "SEARCH_TYPE_RU");

        GenSearchResponse? parsed;
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v2/gen/search")
            {
                Content = JsonContent.Create(request, options: JsonOptions),
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Api-Key", opts.ApiKey);

            using var response = await httpClient.SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();

            // Ответ — JSON-массив (обычно из одного элемента вне режима getPartialResults) —
            // подтверждено живым запросом, расходится с примером тела ответа в документации.
            var chunks = await response.Content.ReadFromJsonAsync<List<GenSearchResponse>>(JsonOptions, ct);
            parsed = chunks?.LastOrDefault();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Yandex Search API недоступен или запрос по «{NormalizedName}» превысил таймаут", normalizedName);
            return [];
        }

        if (parsed is null || parsed.IsAnswerRejected || parsed.ProblematicAnswer || parsed.Message is null
            || string.IsNullOrWhiteSpace(parsed.Message.Content))
        {
            logger.LogInformation(
                "Yandex Search по «{NormalizedName}»: ответ отклонён, помечен как проблемный или пуст.", normalizedName);
            return [];
        }

        var usedSources = (parsed.Sources ?? [])
            .Where(s => s.Used && !string.IsNullOrWhiteSpace(s.Url))
            .ToList();

        if (usedSources.Count == 0)
        {
            logger.LogInformation("Yandex Search по «{NormalizedName}»: ни один источник не использован в ответе.", normalizedName);
            return [];
        }

        // GenSearch отдаёт ОДИН сгенерированный ответ, а не сниппет на источник — но
        // MedicationSummarizer нумерует переданные сниппеты и требует сослаться на индекс
        // (антигаллюцинационный гейт), поэтому один и тот же текст ответа дублируется на каждый
        // использованный источник, чтобы у модели был явный [N] на что сослаться.
        return usedSources
            .Select(s => new WebSnippet(s.Title ?? string.Empty, s.Url!, parsed.Message.Content))
            .ToList();
    }

    // --- DTO запроса/ответа Yandex Web Search API (GenSearch.Search, только нужные поля) ---

    private sealed record GenSearchRequest(
        [property: JsonPropertyName("messages")] List<GenSearchMessage> Messages,
        [property: JsonPropertyName("folderId")] string FolderId,
        [property: JsonPropertyName("fixMisspell")] bool FixMisspell,
        [property: JsonPropertyName("searchType")] string SearchType);

    private sealed record GenSearchMessage(
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("role")] string Role);

    private sealed record GenSearchResponse(
        [property: JsonPropertyName("message")] GenSearchMessage? Message,
        [property: JsonPropertyName("sources")] List<SourceDto>? Sources,
        [property: JsonPropertyName("isAnswerRejected")] bool IsAnswerRejected,
        [property: JsonPropertyName("problematicAnswer")] bool ProblematicAnswer);

    private sealed record SourceDto(
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("used")] bool Used);
}
