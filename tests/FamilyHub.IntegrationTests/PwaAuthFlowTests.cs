using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyHub.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Сквозной PWA-вход (этап 2 п.2.4): регистрация email→код→пароль, cookie-сессия,
/// lockout после серии неверных паролей. Клиент WebApplicationFactory по умолчанию
/// сохраняет cookie между запросами (HandleCookies=true) — сессия работает как в браузере.
/// </summary>
public class PwaAuthFlowTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private static string FreshEmail() => $"user-{Guid.NewGuid():N}@example.com";

    private static string FreshUsername() => $"user{Guid.NewGuid():N}"[..20];

    private async Task<(HttpClient Client, string Email)> RegisterAsync(string password = "Passw0rd", string? username = null)
    {
        var client = AnonymousClient();
        var email = FreshEmail();

        (await client.PostAsJsonAsync("/api/auth/register/start", new { email }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var code = Factory.Emails.LastCodeFor(email);
        code.Should().NotBeNullOrEmpty();

        var confirm = await client.PostAsJsonAsync("/api/auth/register/confirm",
            new
            {
                email, code, password, username = username ?? FreshUsername(),
                lastName = "Пользователев", firstName = "PWA", middleName = (string?)null,
                birthDate = new DateOnly(1990, 1, 1), gender = 0,
            });
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
        await CsrfTestHelper.CaptureCsrfTokenAsync(client);

        return (client, email);
    }

    [Fact]
    public async Task RegisterFlow_SendsStyledHtmlEmail_WithSiteLinkFromConfig()
    {
        // Регресс-сеть на проводку Email:PublicSiteUrl (FamilyHubWebFactory задаёт
        // "https://test.familyhub.local") и на то, что рендерер реально прогоняется через
        // настоящий DI-граф в интеграционном тесте, а не только в юнитах рендерера.
        var client = AnonymousClient();
        var email = FreshEmail();
        (await client.PostAsJsonAsync("/api/auth/register/start", new { email }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var message = Factory.Emails.MessagesFor(email).Should().ContainSingle().Subject;
        message.Html.Should().NotBeNullOrEmpty();
        message.Html.Should().NotContain("{{", "незаполненный плейсхолдер не должен уходить пользователю");
        message.Html.Should().Contain("https://test.familyhub.local");
        message.Subject.Should().Be("FamilyHub: код для регистрации");
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
        me.LastName.Should().Be("Пользователев");
        me.FirstName.Should().Be("PWA");
    }

    [Fact]
    public async Task Login_WithRegisteredPassword_Succeeds_AndWrongPassword_Returns401()
    {
        var (_, email) = await RegisterAsync(password: "Str0ngPw");
        var freshClient = AnonymousClient();

        (await freshClient.PostAsJsonAsync("/api/auth/login", new { email, password = "Wr0ngPwd" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await freshClient.PostAsJsonAsync("/api/auth/login", new { email, password = "Str0ngPw" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await freshClient.GetAsync("/api/families")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_FiveWrongPasswords_Returns423_EvenForCorrectPassword()
    {
        var (_, email) = await RegisterAsync(password: "Str0ngPw");
        var client = AnonymousClient();

        for (var i = 0; i < 4; i++)
            (await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Wr0ngPwd" }))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Wr0ngPwd" }))
            .StatusCode.Should().Be(HttpStatusCode.Locked);
        (await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Str0ngPw" }))
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

    // Регрессия на находку 4 (аудит module-review-2026-08-02/01-auth-identity.md): мутирующий
    // PWA-cookie запрос без CSRF-заголовка обязан отклоняться, даже если сама PWA-сессия валидна.
    [Fact]
    public async Task MutatingPwaRequest_WithoutCsrfHeader_Returns400()
    {
        var (client, _) = await RegisterAsync();
        // Симулируем клиента без withXsrfConfiguration (не-Angular caller/сторонний сайт).
        client.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");

        var response = await client.PostAsync("/api/auth/logout", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        body.GetProperty("code").GetString().Should().Be("csrf_token_invalid");

        // Сессия при этом НЕ должна быть тронута (logout не выполнился) — GET по-прежнему проходит.
        (await client.GetAsync("/api/families")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // Регрессия на инцидент 2026-08-20: после включения персистентных ключей Data Protection
    // анонимный POST /api/auth/login с "чужой" CSRF-парой от ПРЕДЫДУЩЕЙ сессии (JWT истёк или
    // отсутствует, но familyhub.csrf/XSRF-TOKEN ещё в браузере — естественное истечение JWT
    // БЕЗ явного /logout их не чистит, см. PwaSessionCookieWriter.ClearSessionCookies) валил
    // логин с "meant for a different claims-based user": IAntiforgery.GetAndStoreTokens
    // привязывает токен к HttpContext.User НА МОМЕНТ ВЫДАЧИ, а на анонимном /login текущий
    // пользователь ещё не определён. CSRF защищает только уже аутентифицированное действие —
    // у анонимного запроса нет сессии, которую можно "прокатить" межсайтовой подделкой, гейт
    // (Program.cs) теперь применяется только когда текущий запрос сам уже аутентифицирован.
    [Fact]
    public async Task Login_WithStaleCsrfCookieFromPreviousSession_Succeeds()
    {
        var previousSessionClient = AnonymousClient();
        var email = FreshEmail();
        const string password = "Passw0rd";

        (await previousSessionClient.PostAsJsonAsync("/api/auth/register/start", new { email }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var code = Factory.Emails.LastCodeFor(email);
        (await previousSessionClient.PostAsJsonAsync("/api/auth/register/confirm",
                new
                {
                    email, code, password, username = FreshUsername(),
                    lastName = "Пользователев", firstName = "PWA", middleName = (string?)null,
                    birthDate = new DateOnly(1990, 1, 1), gender = 0,
                }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // /me переиздаёт CSRF-пару на каждый вызов (см. AuthEndpoints.MapAuthEndpoints, "/me") —
        // тот же путь, которым реальный SPA получает токен при старте (app.component.ts).
        var me = await previousSessionClient.GetAsync("/api/auth/me");
        var (privateCookie, publicCookie, xsrfHeaderValue) = ExtractCsrfCookiePair(me);

        // Клиент БЕЗ автоматического cookie-jar и БЕЗ единой JWT-cookie — эмулирует браузер без
        // валидной сессии, у которого осталась только пара CSRF-cookie от прошлого раза.
        var anonymousBrowserWithStaleCookies = Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email, password }),
        };
        request.Headers.Add("Cookie", $"{privateCookie}; {publicCookie}");
        request.Headers.Add("X-XSRF-TOKEN", xsrfHeaderValue);

        var response = await anonymousBrowserWithStaleCookies.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static (string PrivateCookie, string PublicCookie, string XsrfHeaderValue) ExtractCsrfCookiePair(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue("/me должен переиздавать CSRF-пару");

        string? privateCookie = null;
        string? publicCookie = null;
        string? xsrfHeaderValue = null;
        foreach (var header in cookies!)
        {
            if (header.StartsWith("familyhub.csrf=", StringComparison.Ordinal))
            {
                privateCookie = header[..header.IndexOf(';')];
            }
            else if (header.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal))
            {
                publicCookie = header[..header.IndexOf(';')];
                xsrfHeaderValue = Uri.UnescapeDataString(publicCookie["XSRF-TOKEN=".Length..]);
            }
        }

        privateCookie.Should().NotBeNull("приватная httpOnly antiforgery-cookie обязана быть в ответе /me");
        publicCookie.Should().NotBeNull("публичная XSRF-TOKEN-cookie обязана быть в ответе /me");
        return (privateCookie!, publicCookie!, xsrfHeaderValue!);
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

        (await telegramClient.PostAsJsonAsync("/api/auth/link-email/confirm", new { email, code, password = "Link3dPw" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var pwaClient = AnonymousClient();
        (await pwaClient.PostAsJsonAsync("/api/auth/login", new { email, password = "Link3dPw" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var me = await (await pwaClient.GetAsync("/api/auth/me")).Content.ReadFromJsonAsync<MeDto>(JsonOpts);
        me!.HasTelegram.Should().BeTrue("это тот же аккаунт, что и Telegram-сессия");
    }

    // Регрессия на аудит module-review-2026-08-02/01-auth-identity.md, находка 6: access-токен —
    // самодостаточный JWT (не ходит в БД на каждый запрос), поэтому остаётся валидным ещё до
    // истечения TTL, даже если строка Users уже удалена конкурентно (слияние аккаунтов,
    // самостоятельное удаление с другого устройства). /me раньше падал 500 (SingleAsync) вместо
    // ожидаемого 401 в этом узком окне.
    [Fact]
    public async Task Me_WithStaleTokenAfterConcurrentAccountDeletion_Returns401NotFail500()
    {
        var (client, email) = await RegisterAsync();
        (await client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK, "аккаунт пока существует");

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(u => u.Email == email);
            db.Users.Remove(user);
            await db.SaveChangesAsync();
        }

        // Тот же клиент — access-токен в cookie не тронут, всё ещё криптографически валиден
        // (JwtBearer ничего не знает про удаление строки, exp ещё не наступил).
        (await client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private record MeDto(
        Guid UserId, string? LastName, string? FirstName, string? MiddleName,
        string Provider, string? Email, bool HasTelegram, bool HasPassword);
}
