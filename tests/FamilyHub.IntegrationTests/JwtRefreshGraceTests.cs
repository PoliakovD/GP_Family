using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Фабрика с включённым grace-окном ротации refresh-токена. Отдельная от <see cref="JwtWebFactory"/>:
/// там окно обнулено, потому что тесты reuse-detection предъявляют ротированный токен сразу и ждут
/// отзыва цепочки. Здесь, наоборот, окно нужно маленьким, но ненулевым (1 с) — повтор в пределах окна
/// должен проходить, а после него — уже считаться кражей. Refresh живёт минуты, чтобы «после окна»
/// проверялось именно окно, а не истечение срока.
/// </summary>
public class JwtGraceWebFactory : FamilyHubWebFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Jwt:AccessTokenLifetime", "00:00:02");
        builder.UseSetting("Jwt:RefreshTokenLifetime", "00:05:00");
        builder.UseSetting("Jwt:ClockSkew", "00:00:00");
        builder.UseSetting("Jwt:RefreshGraceWindow", "00:00:01");
    }
}

[CollectionDefinition(Name)]
public class JwtGraceCollection : ICollectionFixture<JwtGraceWebFactory>
{
    public const string Name = "JwtGraceIntegration";
}

/// <summary>
/// Grace-окно повторного предъявления ротированного refresh-токена (прод-инцидент 2026-10-30:
/// вторая вкладка/PWA предъявила только что ротированный токен через 7–25 мс, сервер увидел
/// reuse-detection и отозвал все сессии). Клиенты без cookie-jar — тест держит в руках токены,
/// которые браузер уже заменил бы.
/// </summary>
[Collection(JwtGraceCollection.Name)]
public class JwtRefreshGraceTests(JwtGraceWebFactory factory)
{
    private const string PwaAt = "familyhub.at";
    private const string PwaRt = "familyhub.rt";
    private const string Password = "Passw0rd";

    private HttpClient RawClient() =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static string ExtractCookie(HttpResponseMessage response, string name)
    {
        var header = response.Headers.GetValues("Set-Cookie")
            .Single(h => h.StartsWith(name + "=", StringComparison.Ordinal));
        return header[(name.Length + 1)..].Split(';')[0];
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, params (string Name, string Value)[] cookies)
    {
        var request = new HttpRequestMessage(method, url);
        if (cookies.Length > 0)
            request.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}")));
        return request;
    }

    private async Task<(string Email, string AccessToken, string RefreshToken)> RegisterAsync(HttpClient client)
    {
        var email = $"grace-{Guid.NewGuid():N}@example.com";

        (await client.PostAsJsonAsync("/api/auth/register/start", new { email }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var confirm = await client.PostAsJsonAsync("/api/auth/register/confirm", new
        {
            email, code = factory.Emails.LastCodeFor(email), password = Password,
            username = $"grace{Guid.NewGuid():N}"[..20], lastName = "Грейсов", firstName = "Ротация",
            middleName = (string?)null, birthDate = new DateOnly(1990, 1, 1), gender = 0,
        });
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);

        return (email, ExtractCookie(confirm, PwaAt), ExtractCookie(confirm, PwaRt));
    }

    [Fact]
    public async Task Refresh_ReusesRotatedTokenWithinGrace_IssuesNewPair_AndKeepsChainAlive()
    {
        var client = RawClient();
        var (_, _, refreshToken1) = await RegisterAsync(client);

        var firstRefresh = await client.SendAsync(Request(HttpMethod.Post, "/api/auth/refresh", (PwaRt, refreshToken1)));
        firstRefresh.StatusCode.Should().Be(HttpStatusCode.OK);

        // Гонка двух контекстов: второй /refresh ушёл до того, как применился Set-Cookie первого.
        var graceRefresh = await client.SendAsync(Request(HttpMethod.Post, "/api/auth/refresh", (PwaRt, refreshToken1)));
        graceRefresh.StatusCode.Should().Be(HttpStatusCode.OK, "повтор в пределах окна — не кража");
        var refreshToken3 = ExtractCookie(graceRefresh, PwaRt);

        var accessToken = ExtractCookie(graceRefresh, PwaAt);
        (await client.SendAsync(Request(HttpMethod.Get, "/api/families", (PwaAt, accessToken))))
            .StatusCode.Should().Be(HttpStatusCode.OK, "выданная в grace-окне пара должна работать");

        // Цепочка жива: наследник grace-ротации ещё может ротироваться (сессии не отозваны).
        (await client.SendAsync(Request(HttpMethod.Post, "/api/auth/refresh", (PwaRt, refreshToken3))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_ReusesRotatedTokenAfterGrace_RevokesWholeChain()
    {
        var client = RawClient();
        var (_, _, refreshToken1) = await RegisterAsync(client);

        var firstRefresh = await client.SendAsync(Request(HttpMethod.Post, "/api/auth/refresh", (PwaRt, refreshToken1)));
        firstRefresh.StatusCode.Should().Be(HttpStatusCode.OK);
        var refreshToken2 = ExtractCookie(firstRefresh, PwaRt);

        await Task.Delay(TimeSpan.FromSeconds(2)); // > Jwt:RefreshGraceWindow (1s)

        (await client.SendAsync(Request(HttpMethod.Post, "/api/auth/refresh", (PwaRt, refreshToken1))))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "вне окна повтор ротированного токена — кража");

        (await client.SendAsync(Request(HttpMethod.Post, "/api/auth/refresh", (PwaRt, refreshToken2))))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "reuse-detection обязана убить всю цепочку");
    }
}
