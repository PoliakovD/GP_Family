using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Фабрика с заниженными лимитами rate limiting — основная фабрика наоборот поднимает их,
/// чтобы обычные тесты не ловили 429. Свой контейнер Postgres (наследование фабрики).
/// </summary>
public class RateLimitedWebFactory : FamilyHubWebFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimiting:AuthPermitLimit", "3");
        builder.UseSetting("RateLimiting:AuthWindowSeconds", "3600");
        builder.UseSetting("RateLimiting:CodePermitLimit", "2");
        builder.UseSetting("RateLimiting:CodeWindowSeconds", "3600");
    }
}

[CollectionDefinition(Name)]
public class RateLimitCollection : ICollectionFixture<RateLimitedWebFactory>
{
    public const string Name = "RateLimitIntegration";
}

[Collection(RateLimitCollection.Name)]
public class AuthRateLimitTests(RateLimitedWebFactory factory)
{
    [Fact]
    public async Task AuthEndpoints_OverIpLimit_Return429()
    {
        var client = factory.CreateClient();

        // Лимит политики "auth" = 3/час на IP: четвёртый запрос отбрасывается.
        for (var i = 0; i < 3; i++)
            (await client.PostAsJsonAsync("/api/auth/login", new { email = $"u{i}@example.com", pin = "0000" }))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await client.PostAsJsonAsync("/api/auth/login", new { email = "u4@example.com", pin = "0000" }))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task CodeIssuing_HasOwnStricterLimit()
    {
        var client = factory.CreateClient();

        // Политика "auth-code" = 2/час: третья выдача кода отбрасывается.
        // Отдельная от "auth" партиция — предыдущий тест её не расходует
        // (но и логин-лимит здесь не мешает: у register/start своя политика).
        for (var i = 0; i < 2; i++)
            (await client.PostAsJsonAsync("/api/auth/register/start", new { email = $"rl{i}@example.com" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.PostAsJsonAsync("/api/auth/register/start", new { email = "rl3@example.com" }))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }
}
