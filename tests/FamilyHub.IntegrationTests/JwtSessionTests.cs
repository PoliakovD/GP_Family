using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Фабрика с укороченными временами жизни JWT (и обнулённым ClockSkew) — тесты экспирации/
/// ротации не ждут реальные 15 минут/14 дней. Отдельная от основной FamilyHubWebFactory: те
/// настройки шарятся со всеми обычными тестами коллекции, которым секундный access-токен
/// сломал бы сценарии (см. AuthRateLimitTests/RateLimitedWebFactory — тот же паттерн).
/// </summary>
public class JwtWebFactory : FamilyHubWebFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Jwt:AccessTokenLifetime", "00:00:02");
        builder.UseSetting("Jwt:RefreshTokenLifetime", "00:00:05");
        builder.UseSetting("Jwt:ClockSkew", "00:00:00");
    }
}

[CollectionDefinition(Name)]
public class JwtCollection : ICollectionFixture<JwtWebFactory>
{
    public const string Name = "JwtIntegration";
}

/// <summary>
/// JWT PWA-сессия: короткоживущий access в httpOnly cookie + ротируемый refresh в БД.
/// Клиенты здесь НЕ используют авто-cookie-jar (HandleCookies=false) — тесты reuse-detection
/// должны держать в руках уже отозванный (ротацией) refresh-токен, а автоматический jar
/// после ротации заменил бы его новым и стёр бы возможность воспроизвести кражу/повтор.
/// </summary>
[Collection(JwtCollection.Name)]
public class JwtSessionTests(JwtWebFactory factory)
{
    private static string FreshEmail() => $"jwt-{Guid.NewGuid():N}@example.com";

    private static string FreshUsername() => $"jwtuser{Guid.NewGuid():N}"[..20];

    private HttpClient RawClient() =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    /// <summary>Регистрирует нового пользователя и возвращает (email, access, refresh) исходной сессии.</summary>
    private async Task<(string Email, string AccessToken, string RefreshToken)> RegisterAsync(
        HttpClient client, string password = "Passw0rd")
    {
        var email = FreshEmail();

        (await client.PostAsJsonAsync("/api/auth/register/start", new { email }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var code = factory.Emails.LastCodeFor(email);
        code.Should().NotBeNullOrEmpty();

        var confirm = await client.PostAsJsonAsync("/api/auth/register/confirm",
            new { email, code, password, username = FreshUsername(), displayName = "JWT Пользователь" });
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);

        return (email, ExtractCookie(confirm, PwaAt), ExtractCookie(confirm, PwaRt));
    }

    private const string PwaAt = "familyhub.at";
    private const string PwaRt = "familyhub.rt";

    private static string ExtractCookie(HttpResponseMessage response, string cookieName)
    {
        response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders).Should().BeTrue(
            $"ответ должен выставлять cookie {cookieName}");

        foreach (var header in setCookieHeaders!)
        {
            if (!header.StartsWith(cookieName + "=", StringComparison.Ordinal)) continue;
            var afterName = header[(cookieName.Length + 1)..];
            var end = afterName.IndexOf(';');
            return end >= 0 ? afterName[..end] : afterName;
        }

        throw new InvalidOperationException($"Cookie {cookieName} не найден в Set-Cookie заголовках ответа.");
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, params (string Name, string Value)[] cookies)
    {
        var request = new HttpRequestMessage(method, url);
        if (cookies.Length > 0)
            request.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}")));
        return request;
    }

    [Fact]
    public async Task AccessToken_ExpiresAfterLifetime_ButRefreshRestoresAccess()
    {
        var client = RawClient();
        var (_, accessToken, refreshToken) = await RegisterAsync(client);

        (await client.SendAsync(Request(HttpMethod.Get, "/api/families", (PwaAt, accessToken))))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await Task.Delay(TimeSpan.FromSeconds(3)); // > Jwt:AccessTokenLifetime (2s), ClockSkew=0

        (await client.SendAsync(Request(HttpMethod.Get, "/api/families", (PwaAt, accessToken))))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var refresh = await client.SendAsync(Request(HttpMethod.Post, "/api/auth/refresh", (PwaRt, refreshToken)));
        refresh.StatusCode.Should().Be(HttpStatusCode.OK);
        var newAccessToken = ExtractCookie(refresh, PwaAt);

        (await client.SendAsync(Request(HttpMethod.Get, "/api/families", (PwaAt, newAccessToken))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_RotatesToken_AndRejectsReuseOfOldOne_RevokingWholeChain()
    {
        var client = RawClient();
        var (_, _, refreshToken1) = await RegisterAsync(client);

        var firstRefresh = await client.SendAsync(Request(HttpMethod.Post, "/api/auth/refresh", (PwaRt, refreshToken1)));
        firstRefresh.StatusCode.Should().Be(HttpStatusCode.OK);
        var refreshToken2 = ExtractCookie(firstRefresh, PwaRt);
        refreshToken2.Should().NotBe(refreshToken1, "ротация обязана выдать новый refresh-токен");

        // Кража/повтор: предъявляем уже заменённый токен ещё раз.
        (await client.SendAsync(Request(HttpMethod.Post, "/api/auth/refresh", (PwaRt, refreshToken1))))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Reuse detection обязана убить всю цепочку — легитимный "наследник" тоже мёртв.
        (await client.SendAsync(Request(HttpMethod.Post, "/api/auth/refresh", (PwaRt, refreshToken2))))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_RevokesRefreshTokenServerSide_ReplayFails()
    {
        var client = RawClient();
        var (_, accessToken, refreshToken) = await RegisterAsync(client);

        var logout = await client.SendAsync(
            Request(HttpMethod.Post, "/api/auth/logout", (PwaAt, accessToken), (PwaRt, refreshToken)));
        logout.StatusCode.Should().Be(HttpStatusCode.OK);

        // Раньше (cookie-модель) logout лишь стирал cookie у клиента — сервер ничего не помнил,
        // поэтому украденный до logout refresh-токен продолжал бы работать. Теперь — настоящий
        // server-side revoke: повтор старого refresh-токена после logout должен провалиться.
        (await client.SendAsync(Request(HttpMethod.Post, "/api/auth/refresh", (PwaRt, refreshToken))))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LogoutAll_RevokesEveryDeviceSession()
    {
        var deviceA = RawClient();
        var (email, accessTokenA, refreshTokenA) = await RegisterAsync(deviceA);

        // "Второе устройство" — тот же пользователь, независимая сессия через повторный логин.
        var deviceB = RawClient();
        var loginB = await deviceB.PostAsJsonAsync("/api/auth/login", new { email, password = "Passw0rd" });
        loginB.StatusCode.Should().Be(HttpStatusCode.OK);
        var refreshTokenB = ExtractCookie(loginB, PwaRt);

        var logoutAll = await deviceA.SendAsync(
            Request(HttpMethod.Post, "/api/auth/logout-all", (PwaAt, accessTokenA)));
        logoutAll.StatusCode.Should().Be(HttpStatusCode.OK);

        (await deviceA.SendAsync(Request(HttpMethod.Post, "/api/auth/refresh", (PwaRt, refreshTokenA))))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await deviceB.SendAsync(Request(HttpMethod.Post, "/api/auth/refresh", (PwaRt, refreshTokenB))))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "logout-all обязан отозвать сессии ВСЕХ устройств");
    }
}
