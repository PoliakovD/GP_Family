using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

[CollectionDefinition(Name)]
public class AdminIntegrationCollection : ICollectionFixture<AdminWebFactory>
{
    public const string Name = "FamilyHub admin integration tests";
}

/// <summary>
/// Сквозной вход в админ-панель (ADR-0009) через реальный DI-граф Program.cs — единственный
/// способ поймать ошибки в проводке AuthSchemes.Admin/политики "PlatformAdmin"/эндпоинтов,
/// которые юнит-тесты отдельных классов не видят (см. AdminWebFactory, Admin:Enabled=true).
/// </summary>
[Collection(AdminIntegrationCollection.Name)]
public class AdminSessionApiTests(AdminWebFactory factory)
{
    private HttpClient AnonymousClient() => factory.CreateClient();

    [Fact]
    public async Task Session_WithoutLogin_Returns401()
    {
        var client = AnonymousClient();

        (await client.GetAsync("/api/admin/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401AndDoesNotSetCookie()
    {
        var client = AnonymousClient();

        var response = await client.PostAsJsonAsync("/api/admin/session",
            new { user = AdminWebFactory.TestUser, password = "wrong-password" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.TryGetValues("Set-Cookie", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Login_WithCorrectCredentials_IssuesSessionThatPassesGuardCheck()
    {
        var client = AnonymousClient();

        var login = await client.PostAsJsonAsync("/api/admin/session",
            new { user = AdminWebFactory.TestUser, password = AdminWebFactory.TestPassword });
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        // HttpClient из WebApplicationFactory сохраняет cookie между запросами (как браузер).
        (await client.GetAsync("/api/admin/session")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logout_ClearsSession_SubsequentCheckReturns401()
    {
        var client = AnonymousClient();
        (await client.PostAsJsonAsync("/api/admin/session",
            new { user = AdminWebFactory.TestUser, password = AdminWebFactory.TestPassword }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.DeleteAsync("/api/admin/session")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.GetAsync("/api/admin/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_WithoutAnyPriorSession_StillReturns200()
    {
        // "Приведи к состоянию X" (см. patterns/backend.md) — вызов без сессии не должен 401'ить,
        // целевое состояние ("вышел") уже достигнуто.
        var client = AnonymousClient();

        (await client.DeleteAsync("/api/admin/session")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AdminCookie_DoesNotGrantAccessToOrdinaryApi()
    {
        // Сессия админ-панели — отдельная схема, никогда не участвующая в Smart-селекторе:
        // обычные /api-эндпоинты её не принимают, cookie не подменяет PWA/Telegram-identity.
        var client = AnonymousClient();
        (await client.PostAsJsonAsync("/api/admin/session",
            new { user = AdminWebFactory.TestUser, password = AdminWebFactory.TestPassword }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.GetAsync("/api/families")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
