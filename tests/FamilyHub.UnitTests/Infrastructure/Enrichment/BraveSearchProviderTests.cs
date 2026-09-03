using System.Net;
using System.Text;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.Prompts;
using FamilyHub.UnitTests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Enrichment;

/// <summary>
/// BraveSearchProvider — возвращает ВСЕ результаты выдачи как есть (пересборка enrich-пайплайна):
/// фильтрация по доверенным доменам и усечение до MaxSnippets переехали на процессор
/// (EnrichmentSnippetFilter, БД-список EnrichmentTrustedDomain — см. их тесты), провайдер больше
/// не знает про доверие домену вообще. Здесь проверяется только сам разбор ответа API и
/// устойчивость к сбоям (таймаут/сеть) — фейковым HttpMessageHandler, без реального обращения к
/// api.search.brave.com.
/// </summary>
public class BraveSearchProviderTests
{
    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    private static BraveSearchProvider CreateSut(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.search.brave.com/") };
        var opts = new EnrichmentOptions { ApiKey = "test-key" };
        var promptProvider = TestPromptProvider.ReturningFallback();
        return new BraveSearchProvider(
            httpClient, Options.Create(opts), NullLogger<BraveSearchProvider>.Instance,
            new AnalyteSearchQueryBuilder(promptProvider), promptProvider);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private const string ResponseWithMixedDomains = """
        {
          "web": {
            "results": [
              { "title": "Видаль", "url": "https://www.vidal.ru/drugs/paracetamol", "description": "Инструкция по применению." },
              { "title": "Спам", "url": "https://spam.example.com/paracetamol", "description": "Купить дёшево." },
              { "title": "РЛС", "url": "https://www.rlsnet.ru/drugs/paracetamol", "description": "Показания к применению." }
            ]
          }
        }
        """;

    [Fact]
    public async Task SearchAsync_ReturnsAllResults_IncludingUntrustedDomains()
    {
        var sut = CreateSut(new FakeHttpMessageHandler(_ => JsonResponse(ResponseWithMixedDomains)));

        var snippets = await sut.SearchAsync("парацетамол");

        snippets.Should().HaveCount(3,
            "провайдер больше не фильтрует по доверенным доменам (пересборка enrich-пайплайна) — это делает процессор");
        snippets.Should().Contain(s => s.Url.Contains("spam.example.com"));
    }

    [Fact]
    public async Task SearchAsync_SkipsResultsWithoutUrlOrDescription()
    {
        const string withGaps = """
            { "web": { "results": [
              { "title": "Без описания", "url": "https://vidal.ru/x" },
              { "title": "Полный", "url": "https://vidal.ru/y", "description": "Текст." }
            ] } }
            """;
        var sut = CreateSut(new FakeHttpMessageHandler(_ => JsonResponse(withGaps)));

        var snippets = await sut.SearchAsync("парацетамол");

        snippets.Should().ContainSingle();
        snippets[0].Url.Should().Be("https://vidal.ru/y");
    }

    [Fact]
    public async Task SearchAsync_EmptyResults_ReturnsEmpty()
    {
        const string empty = """{ "web": { "results": [] } }""";
        var sut = CreateSut(new FakeHttpMessageHandler(_ => JsonResponse(empty)));

        var snippets = await sut.SearchAsync("парацетамол");

        snippets.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_NetworkFailure_ReturnsEmpty_DoesNotThrow()
    {
        var sut = CreateSut(new ThrowingHttpMessageHandler(new HttpRequestException("Тестовый сбой сети")));

        var snippets = await sut.SearchAsync("парацетамол");

        snippets.Should().BeEmpty("сетевой сбой не должен ронять весь конвейер обогащения — только эту попытку поиска");
    }

    [Fact]
    public async Task SearchAsync_Timeout_ReturnsEmpty_DoesNotThrow()
    {
        var sut = CreateSut(new ThrowingHttpMessageHandler(new TaskCanceledException("Тестовый таймаут")));

        var snippets = await sut.SearchAsync("парацетамол");

        snippets.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_Medication_UsesAdminTemplate_FromPromptProvider_WithNameSubstituted()
    {
        Uri? requestUri = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            requestUri = request.RequestUri;
            return JsonResponse("""{ "web": { "results": [] } }""");
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.search.brave.com/") };
        var promptProvider = Substitute.For<IPromptProvider>();
        promptProvider.GetAsync("medication.search-query.brave", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("{name} — купить в аптеке РФ"));
        var sut = new BraveSearchProvider(
            httpClient, Options.Create(new EnrichmentOptions { ApiKey = "test-key" }),
            NullLogger<BraveSearchProvider>.Instance, new AnalyteSearchQueryBuilder(promptProvider), promptProvider);

        await sut.SearchAsync("парацетамол", WebSearchTopic.Medication);

        requestUri!.Query.Should().Contain(Uri.EscapeDataString("парацетамол — купить в аптеке РФ"),
            "текст запроса для медикамента должен браться из шаблона IPromptProvider, а не из захардкоженной строки");
    }
}
