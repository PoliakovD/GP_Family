using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FamilyHub.Infrastructure.Enrichment;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Enrichment;

/// <summary>
/// YandexSearchProvider — GenSearch отдаёт один сгенерированный ответ (message.content) вместо
/// сниппета на источник, поэтому фильтрация по used+TrustedDomains и обработка
/// isAnswerRejected/problematicAnswer здесь важнее, чем для Brave. Фейковый HttpMessageHandler —
/// без реального обращения к searchapi.api.cloud.yandex.net.
/// </summary>
public class YandexSearchProviderTests
{
    private sealed class CapturingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return Task.FromResult(respond(request));
        }
    }

    private sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    private static (YandexSearchProvider Sut, CapturingHttpMessageHandler Handler) CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage> respond, EnrichmentOptions? options = null)
    {
        var handler = new CapturingHttpMessageHandler(respond);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://searchapi.api.cloud.yandex.net/") };
        var opts = options ?? new EnrichmentOptions
        {
            ApiKey = "test-key",
            FolderId = "b1gtest0000000000000",
            TrustedDomains = ["vidal.ru", "rlsnet.ru"],
            MaxSnippets = 5,
        };
        return (new YandexSearchProvider(httpClient, Options.Create(opts), NullLogger<YandexSearchProvider>.Instance), handler);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    // Ответ Yandex приходит ОБЁРНУТЫМ В МАССИВ (gRPC-gateway streaming-стиль) — подтверждено
    // живым запросом к API, расходится с примером тела ответа в документации.
    private const string ResponseWithUsedAndUnusedTrustedSources = """
        [{
          "message": { "content": "Парацетамол — жаропонижающее средство.", "role": "ROLE_ASSISTANT" },
          "sources": [
            { "url": "https://www.vidal.ru/drugs/paracetamol", "title": "Видаль", "used": true },
            { "url": "https://www.rlsnet.ru/drugs/paracetamol", "title": "РЛС", "used": false },
            { "url": "https://spam.example.com/x", "title": "Спам", "used": true }
          ],
          "isAnswerRejected": false,
          "problematicAnswer": false
        }]
        """;

    [Fact]
    public async Task SearchAsync_OnlyUsedAndTrustedSources_AreReturned()
    {
        var (sut, _) = CreateSut(_ => JsonResponse(ResponseWithUsedAndUnusedTrustedSources));

        var snippets = await sut.SearchAsync("парацетамол");

        snippets.Should().HaveCount(1, "неиспользованный (used=false) и недоверенный источники должны быть отброшены");
        snippets[0].Url.Should().Be("https://www.vidal.ru/drugs/paracetamol");
        snippets[0].Text.Should().Contain("жаропонижающее", "текст ответа GenSearch дублируется на каждый used+доверенный источник");
    }

    [Fact]
    public async Task SearchAsync_AnswerRejected_ReturnsEmpty()
    {
        const string rejected = """
            [{ "message": { "content": "текст", "role": "ROLE_ASSISTANT" }, "sources": [],
              "isAnswerRejected": true, "problematicAnswer": false }]
            """;
        var (sut, _) = CreateSut(_ => JsonResponse(rejected));

        var snippets = await sut.SearchAsync("парацетамол");

        snippets.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_ProblematicAnswer_ReturnsEmpty()
    {
        const string problematic = """
            [{ "message": { "content": "текст", "role": "ROLE_ASSISTANT" },
              "sources": [ { "url": "https://vidal.ru/x", "title": "Видаль", "used": true } ],
              "isAnswerRejected": false, "problematicAnswer": true }]
            """;
        var (sut, _) = CreateSut(_ => JsonResponse(problematic));

        var snippets = await sut.SearchAsync("парацетамол");

        snippets.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_NoUsedTrustedSources_ReturnsEmpty()
    {
        const string onlyUnused = """
            [{ "message": { "content": "текст", "role": "ROLE_ASSISTANT" },
              "sources": [ { "url": "https://vidal.ru/x", "title": "Видаль", "used": false } ],
              "isAnswerRejected": false, "problematicAnswer": false }]
            """;
        var (sut, _) = CreateSut(_ => JsonResponse(onlyUnused));

        var snippets = await sut.SearchAsync("парацетамол");

        snippets.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_NetworkFailure_ReturnsEmpty_DoesNotThrow()
    {
        var handler = new ThrowingHttpMessageHandler(new HttpRequestException("Тестовый сбой сети"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://searchapi.api.cloud.yandex.net/") };
        var sut = new YandexSearchProvider(
            httpClient,
            Options.Create(new EnrichmentOptions { ApiKey = "k", FolderId = "f", TrustedDomains = ["vidal.ru"] }),
            NullLogger<YandexSearchProvider>.Instance);

        var snippets = await sut.SearchAsync("парацетамол");

        snippets.Should().BeEmpty("сетевой сбой не должен ронять конвейер обогащения");
    }

    [Fact]
    public async Task SearchAsync_SendsApiKeyHeader_AndFolderId_WithoutRestrictingHost()
    {
        var (sut, handler) = CreateSut(_ => JsonResponse(ResponseWithUsedAndUnusedTrustedSources));

        await sut.SearchAsync("парацетамол");

        handler.LastRequest!.Headers.Authorization.Should().BeEquivalentTo(new AuthenticationHeaderValue("Api-Key", "test-key"));
        handler.LastRequestBody.Should().Contain("\"folderId\":\"b1gtest0000000000000\"");
        // "host" в теле запроса намеренно не отправляем (см. класс-докстринг YandexSearchProvider) —
        // живой запрос показал, что ограничение поиска этим полем даёт "Ничего не найдено" даже для
        // доменов, которые находятся и используются без ограничения; фильтрация — постфактум.
        handler.LastRequestBody.Should().NotContain("\"host\"");
    }
}
