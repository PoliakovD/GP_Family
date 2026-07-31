using System.Net;
using System.Text;
using FamilyHub.Infrastructure.Enrichment;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Enrichment;

/// <summary>
/// BraveSearchProvider — фильтрация по доверенным доменам и усечение до MaxSnippets происходят
/// на нашей стороне (не через `site:`-синтаксис поисковика, см. ADR-0005) — эти правила и
/// устойчивость к сбоям (таймаут/сеть) покрыты здесь фейковым HttpMessageHandler, без реального
/// обращения к api.search.brave.com.
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

    private static BraveSearchProvider CreateSut(HttpMessageHandler handler, EnrichmentOptions? options = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.search.brave.com/") };
        var opts = options ?? new EnrichmentOptions
        {
            ApiKey = "test-key",
            TrustedDomains = ["vidal.ru", "rlsnet.ru"],
            MaxSnippets = 5,
        };
        return new BraveSearchProvider(httpClient, Options.Create(opts), NullLogger<BraveSearchProvider>.Instance);
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
    public async Task SearchAsync_FiltersOutUntrustedDomains()
    {
        var sut = CreateSut(new FakeHttpMessageHandler(_ => JsonResponse(ResponseWithMixedDomains)));

        var snippets = await sut.SearchAsync("парацетамол");

        snippets.Should().HaveCount(2, "спам-домен не входит в TrustedDomains и должен быть отброшен");
        snippets.Should().OnlyContain(s => s.Url.Contains("vidal.ru") || s.Url.Contains("rlsnet.ru"));
    }

    [Fact]
    public async Task SearchAsync_TrustsSubdomainsOfTrustedDomain()
    {
        // "www.vidal.ru" доверен, если доверен "vidal.ru" (см. IsTrustedDomain).
        var sut = CreateSut(new FakeHttpMessageHandler(_ => JsonResponse(ResponseWithMixedDomains)));

        var snippets = await sut.SearchAsync("парацетамол");

        snippets.Should().Contain(s => s.Url == "https://www.vidal.ru/drugs/paracetamol");
    }

    [Fact]
    public async Task SearchAsync_TruncatesToMaxSnippets()
    {
        var sut = CreateSut(
            new FakeHttpMessageHandler(_ => JsonResponse(ResponseWithMixedDomains)),
            new EnrichmentOptions { ApiKey = "test-key", TrustedDomains = ["vidal.ru", "rlsnet.ru"], MaxSnippets = 1 });

        var snippets = await sut.SearchAsync("парацетамол");

        snippets.Should().HaveCount(1, "MaxSnippets=1 должен обрезать даже при двух доверенных совпадениях");
    }

    [Fact]
    public async Task SearchAsync_NoTrustedResults_ReturnsEmpty()
    {
        const string onlyUntrusted = """
            { "web": { "results": [ { "title": "Спам", "url": "https://spam.example.com/x", "description": "..." } ] } }
            """;
        var sut = CreateSut(new FakeHttpMessageHandler(_ => JsonResponse(onlyUntrusted)));

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
}
