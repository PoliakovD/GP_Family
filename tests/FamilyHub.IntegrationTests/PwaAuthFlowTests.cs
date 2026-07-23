using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Сквозной PWA-вход (этап 2 п.2.4): регистрация email→код→PIN, cookie-сессия,
/// lockout после серии неверных PIN. Клиент WebApplicationFactory по умолчанию
/// сохраняет cookie между запросами (HandleCookies=true) — сессия работает как в браузере.
/// </summary>
public class PwaAuthFlowTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private static string FreshEmail() => $"user-{Guid.NewGuid():N}@example.com";

    private static string FreshUsername() => $"user{Guid.NewGuid():N}"[..20];

    private async Task<(HttpClient Client, string Email)> RegisterAsync(string pin = "1234", string? username = null)
    {
        var client = AnonymousClient();
        var email = FreshEmail();

        (await client.PostAsJsonAsync("/api/auth/register/start", new { email }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var code = Factory.Emails.LastCodeFor(email);
        code.Should().NotBeNullOrEmpty();

        var confirm = await client.PostAsJsonAsync("/api/auth/register/confirm",
            new { email, code, pin, username = username ?? FreshUsername(), displayName = "PWA Пользователь" });
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);

        return (client, email);
    }

    [Fact]
    public async Task RegisterFlow_IssuesCookieSession_ThatAccessesApi()
    {
        var (client, _) = await RegisterAsync();

        // Cookie-сессия открывает обычные API (FallbackPolicy пропускает аутентифицированных).
        var families = await client.GetAsync("/api/families");
        families.StatusCode.Should().Be(HttpStatusCode.OK);

        var me = await (await client.GetAsync("/api/auth/me")).Content.ReadFromJsonAsync<MeDto>(JsonOpts);
        me!.Provider.Should().Be("email");
        me.HasTelegram.Should().BeFalse();
        me.DisplayName.Should().Be("PWA Пользователь");
    }

    [Fact]
    public async Task Login_WithRegisteredPin_Succeeds_AndWrongPin_Returns401()
    {
        var (_, email) = await RegisterAsync(pin: "9876");
        var freshClient = AnonymousClient();

        (await freshClient.PostAsJsonAsync("/api/auth/login", new { email, pin = "0000" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await freshClient.PostAsJsonAsync("/api/auth/login", new { email, pin = "9876" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await freshClient.GetAsync("/api/families")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_FiveWrongPins_Returns423_EvenForCorrectPin()
    {
        var (_, email) = await RegisterAsync(pin: "9876");
        var client = AnonymousClient();

        for (var i = 0; i < 4; i++)
            (await client.PostAsJsonAsync("/api/auth/login", new { email, pin = "0000" }))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await client.PostAsJsonAsync("/api/auth/login", new { email, pin = "0000" }))
            .StatusCode.Should().Be(HttpStatusCode.Locked);
        (await client.PostAsJsonAsync("/api/auth/login", new { email, pin = "9876" }))
            .StatusCode.Should().Be(HttpStatusCode.Locked);
    }

    [Fact]
    public async Task Logout_InvalidatesCookieSession()
    {
        var (client, _) = await RegisterAsync();

        (await client.PostAsync("/api/auth/logout", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.GetAsync("/api/families")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnonymousWithoutCookie_IsRejected()
    {
        (await AnonymousClient().GetAsync("/api/families")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TelegramDevHeaderPath_StillWorks_AlongsideCookieScheme()
    {
        // Смена дефолтной схемы на Smart не должна ломать Telegram/dev-путь.
        (await ClientAs(FreshTelegramId()).GetAsync("/api/families")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task LinkEmail_FromTelegramSession_EnablesPwaLogin()
    {
        var telegramClient = ClientAs(FreshTelegramId());
        var email = FreshEmail();

        (await telegramClient.PostAsJsonAsync("/api/auth/link-email/start", new { email }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var code = Factory.Emails.LastCodeFor(email);

        (await telegramClient.PostAsJsonAsync("/api/auth/link-email/confirm", new { email, code, pin = "2468" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var pwaClient = AnonymousClient();
        (await pwaClient.PostAsJsonAsync("/api/auth/login", new { email, pin = "2468" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var me = await (await pwaClient.GetAsync("/api/auth/me")).Content.ReadFromJsonAsync<MeDto>(JsonOpts);
        me!.HasTelegram.Should().BeTrue("это тот же аккаунт, что и Telegram-сессия");
    }

    private record MeDto(Guid UserId, string DisplayName, string Provider, string? Email, bool HasTelegram, bool HasPin);
}
