using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Регрессия на находку M5 (docs/security/security-audit-2026-10.md): отзыв сессии действует на уже
/// выданный access-токен сразу, а не через AccessTokenLifetime. Обычная фабрика (access живёт 15 мин),
/// а не JwtWebFactory: там access истекает за 2 с, и 401 после отзыва нельзя было бы отличить от истечения.
/// Клиенты без cookie-jar — тест держит в руках «украденный» access-токен.
/// </summary>
public class SessionRevocationTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private const string PwaAt = "familyhub.at";
    private const string PwaRt = "familyhub.rt";
    private const string Password = "Passw0rd";

    private HttpClient RawClient() =>
        Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static string ExtractCookie(HttpResponseMessage response, string name)
    {
        var header = response.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith(name + "=", StringComparison.Ordinal));
        return header[(name.Length + 1)..].Split(';')[0];
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, params (string Name, string Value)[] cookies)
    {
        var request = new HttpRequestMessage(method, url);
        if (cookies.Length > 0)
            request.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}")));
        return request;
    }

    private async Task<(string Email, string Access, string Refresh)> RegisterAsync()
    {
        var client = RawClient();
        var email = $"revoke-{Guid.NewGuid():N}@example.com";
        (await client.PostAsJsonAsync("/api/auth/register/start", new { email })).StatusCode.Should().Be(HttpStatusCode.OK);
        var confirm = await client.PostAsJsonAsync("/api/auth/register/confirm", new
        {
            email, code = Factory.Emails.LastCodeFor(email), password = Password,
            username = $"rev{Guid.NewGuid():N}"[..20], lastName = "Отзывов", firstName = "Сеанс",
            middleName = (string?)null, birthDate = new DateOnly(1990, 1, 1), gender = 0,
        });
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
        return (email, ExtractCookie(confirm, PwaAt), ExtractCookie(confirm, PwaRt));
    }

    /// <summary>Мутирующий PWA-запрос с CSRF-парой из GET /api/auth/me (см. CsrfGateMiddleware).</summary>
    private async Task<HttpResponseMessage> PostWithCsrfAsync(string url, string access, params (string, string)[] extraCookies)
    {
        var client = RawClient();
        var me = await client.SendAsync(Request(HttpMethod.Get, "/api/auth/me", (PwaAt, access)));
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        var csrfHeader = Uri.UnescapeDataString(ExtractCookie(me, "XSRF-TOKEN"));
        var csrfCookie = ("familyhub.csrf", ExtractCookie(me, "familyhub.csrf"));

        var request = Request(HttpMethod.Post, url, [(PwaAt, access), csrfCookie, .. extraCookies]);
        request.Headers.Add("X-XSRF-TOKEN", csrfHeader);
        return await client.SendAsync(request);
    }

    private async Task<HttpStatusCode> GetFamiliesAsync(string access) =>
        (await RawClient().SendAsync(Request(HttpMethod.Get, "/api/families", (PwaAt, access)))).StatusCode;

    [Fact]
    public async Task Logout_InvalidatesAlreadyIssuedAccessToken()
    {
        var (_, access, refresh) = await RegisterAsync();
        (await GetFamiliesAsync(access)).Should().Be(HttpStatusCode.OK);

        (await PostWithCsrfAsync("/api/auth/logout", access, (PwaRt, refresh))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetFamiliesAsync(access)).Should().Be(HttpStatusCode.Unauthorized,
            "украденный до выхода access-токен не должен переживать выход");
    }

    [Fact]
    public async Task LogoutAll_FromAnotherDevice_InvalidatesAccessTokenOfFirstDevice()
    {
        var (email, accessA, _) = await RegisterAsync();
        var loginB = await RawClient().PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        loginB.StatusCode.Should().Be(HttpStatusCode.OK);
        var accessB = ExtractCookie(loginB, PwaAt);
        (await GetFamiliesAsync(accessA)).Should().Be(HttpStatusCode.OK);

        (await PostWithCsrfAsync("/api/auth/logout-all", accessB)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetFamiliesAsync(accessA)).Should().Be(HttpStatusCode.Unauthorized);
        (await GetFamiliesAsync(accessB)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ResetPassword_InvalidatesAccessTokenOfStolenSession()
    {
        var (email, stolenAccess, _) = await RegisterAsync();
        (await GetFamiliesAsync(stolenAccess)).Should().Be(HttpStatusCode.OK);

        var client = RawClient();
        (await client.PostAsJsonAsync("/api/auth/reset-password/start", new { email })).StatusCode.Should().Be(HttpStatusCode.OK);
        var confirm = await client.PostAsJsonAsync("/api/auth/reset-password/confirm",
            new { email, code = Factory.Emails.LastCodeFor(email), newPassword = "N3wPassw0rd" });
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetFamiliesAsync(stolenAccess)).Should().Be(HttpStatusCode.Unauthorized);
        (await GetFamiliesAsync(ExtractCookie(confirm, PwaAt))).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_Rotation_IsNotLogout_OldAccessTokenStillWorks()
    {
        // Ротация refresh помечает старую строку сессии RevokedAt + ReplacedByTokenId, но это не выход:
        // параллельные запросы со старым access-токеном не должны ломаться посреди обычной работы.
        var (_, access, refresh) = await RegisterAsync();

        var rotated = await RawClient().SendAsync(Request(HttpMethod.Post, "/api/auth/refresh", (PwaRt, refresh)));
        rotated.StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetFamiliesAsync(access)).Should().Be(HttpStatusCode.OK);
        (await GetFamiliesAsync(ExtractCookie(rotated, PwaAt))).Should().Be(HttpStatusCode.OK);
    }
}
