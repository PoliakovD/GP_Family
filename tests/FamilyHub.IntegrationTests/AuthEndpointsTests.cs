using System.Net;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Проверяет, что весь HTTP-конвейер аутентификации/авторизации реально включён в тестовом
/// хосте (Development => "Smart"-схема, FallbackPolicy требует аутентификацию по умолчанию),
/// а не подменён/выключен для тестов.
/// </summary>
public class AuthEndpointsTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task Get_WithoutAuthHeader_Returns401()
    {
        var response = await AnonymousClient().GetAsync("/api/families");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_WithDevTelegramHeader_Returns200()
    {
        var response = await ClientAs(FreshTelegramId()).GetAsync("/api/families");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Post_WithoutAuthHeader_Returns401()
    {
        // Защита по умолчанию (FallbackPolicy = DefaultPolicy) касается не только GET —
        // не-GET защищённый эндпоинт без аутентификации тоже должен требовать её.
        var response = await AnonymousClient().PostAsJsonAsync("/api/families", new { Name = "Никогда не создастся" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
