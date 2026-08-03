using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Telegram.Bot;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Отдельная от основной FamilyHubWebFactory (BotToken там пустой намеренно — TelegramInitDataValidator
/// без него отклоняет любую initData) — только здесь бот-токен непустой, чтобы можно было
/// сконструировать реально валидную (по HMAC) initData для /api/auth/telegram/*. Непустой
/// BotToken в Program.cs также поднимает ITelegramBotClient + TelegramWebhookRegistrar
/// (хостед-сервис) — как и в BotWebhookWebFactory, подменяем клиент NSubstitute-моком и держим
/// WebhookUrl пустым, чтобы ничего не стучалось в реальный Telegram API при старте хоста.
/// </summary>
public class TelegramBindingWebFactory : FamilyHubWebFactory
{
    public const string BotToken = "test-bot-token-not-real";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Telegram:BotToken", BotToken);
        builder.UseSetting("Telegram:WebhookUrl", "");

        builder.ConfigureServices(services =>
        {
            services.AddSingleton(Substitute.For<ITelegramBotClient>());
        });
    }
}

[CollectionDefinition(Name)]
public class TelegramBindingCollection : ICollectionFixture<TelegramBindingWebFactory>
{
    public const string Name = "TelegramBindingIntegration";
}

/// <summary>
/// Сквозной сценарий email-as-anchor привязки: TelegramMiniAppAuthenticationHandler — lookup-only
/// (см. TelegramMiniAppAuthenticationHandlerTests), единственный способ авторизовать ещё не
/// привязанный TelegramId — пройти /api/auth/telegram/init → send-code → bind.
/// </summary>
[Collection(TelegramBindingCollection.Name)]
public class TelegramBindingFlowTests(TelegramBindingWebFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Воспроизводит официальный алгоритм подписи initData (см. TelegramInitDataValidator).</summary>
    private static string BuildSignedInitData(long telegramId, string? firstName = "Test", string? lastName = null)
    {
        var userJson = lastName is null
            ? $"{{\"id\":{telegramId},\"first_name\":\"{firstName}\"}}"
            : $"{{\"id\":{telegramId},\"first_name\":\"{firstName}\",\"last_name\":\"{lastName}\"}}";
        var fields = new Dictionary<string, string>
        {
            ["auth_date"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            ["query_id"] = "AAA-test-query-id",
            ["user"] = userJson,
        };

        var dataCheckString = string.Join('\n', fields.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
        var secretKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(TelegramBindingWebFactory.BotToken));
        var hash = Convert.ToHexStringLower(HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(dataCheckString)));

        var query = fields.ToDictionary(kv => kv.Key, kv => Uri.EscapeDataString(kv.Value));
        query["hash"] = hash;
        return string.Join('&', query.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private static long FreshTelegramId() => Random.Shared.NextInt64(1_000_000_000, 9_000_000_000);

    private async Task<(string Email, HttpClient Client, Guid FamilyId)> RegisterPwaUserWithFamilyAsync()
    {
        var email = $"tgbind-{Guid.NewGuid():N}@example.com";
        var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/auth/register/start", new { email }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var code = factory.Emails.LastCodeFor(email);
        var confirm = await client.PostAsJsonAsync("/api/auth/register/confirm", new
        {
            email,
            code,
            password = "Passw0rd",
            username = $"tguser{Guid.NewGuid():N}"[..20],
            displayName = "TG Bind PWA User",
        });
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
        await CsrfTestHelper.CaptureCsrfTokenAsync(client);

        var familyResponse = await client.PostAsJsonAsync("/api/families", new { name = "Привязка Telegram — семья" });
        familyResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var family = await familyResponse.Content.ReadFromJsonAsync<CreatedFamilyDto>(JsonOpts);

        return (email, client, family!.Id);
    }

    private HttpClient TelegramClient(long telegramId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"tma {BuildSignedInitData(telegramId)}");
        return client;
    }

    [Fact]
    public async Task Init_UnboundTelegramId_ReturnsBindingRequired_AndDoesNotCreateUser()
    {
        var telegramId = FreshTelegramId();

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/telegram/init", new { initData = BuildSignedInitData(telegramId) });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<BoundDto>(JsonOpts);
        body!.Bound.Should().BeFalse();

        // Регрессия split-identity: до привязки любой обычный запрос с этим TelegramId 401-ится,
        // а не молча создаёт "голого" пользователя (см. TelegramMiniAppAuthenticationHandler).
        (await TelegramClient(telegramId).GetAsync("/api/families")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task BindFlow_EmailMatchesExistingPwaAccount_SeesSameFamilyData()
    {
        var (email, _, familyId) = await RegisterPwaUserWithFamilyAsync();
        var telegramId = FreshTelegramId();

        (await factory.CreateClient().PostAsJsonAsync("/api/auth/telegram/init", new { initData = BuildSignedInitData(telegramId) }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/telegram/send-code", new { email, initData = BuildSignedInitData(telegramId) }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var messagesBeforeBind = factory.Emails.MessagesFor(email).Count;
        var code = factory.Emails.LastCodeFor(email);

        var bindResponse = await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/telegram/bind", new { email, code, initData = BuildSignedInitData(telegramId) });
        bindResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Тот же аккаунт: Telegram-запрос видит СЕМЬЮ, созданную ранее через PWA-сессию.
        var telegramClient = TelegramClient(telegramId);
        var families = await telegramClient.GetFromJsonAsync<List<FamilyDto>>("/api/families", JsonOpts);
        families.Should().ContainSingle(f => f.Id == familyId);

        var me = await telegramClient.GetFromJsonAsync<MeDto>("/api/auth/me", JsonOpts);
        me!.HasTelegram.Should().BeTrue();
        me.Email.Should().Be(email);

        // У аккаунта уже был пароль (PWA-регистрация выше) — bind не должен слать ничего
        // дополнительного, только письмо с самим OTP-кодом, которое уже учтено в снапшоте.
        factory.Emails.MessagesFor(email).Should().HaveCount(messagesBeforeBind, "у аккаунта уже есть пароль — лишнего письма быть не должно");

        // И исходный пароль PWA-аккаунта по-прежнему рабочий — bind его не тронул.
        (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = "Passw0rd" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BindFlow_NewEmail_CreatesIndependentAccount()
    {
        var telegramId = FreshTelegramId();
        var email = $"tgnew-{Guid.NewGuid():N}@example.com";

        (await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/telegram/send-code", new { email, initData = BuildSignedInitData(telegramId) }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var code = factory.Emails.LastCodeFor(email);

        (await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/telegram/bind", new { email, code, initData = BuildSignedInitData(telegramId) }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var me = await TelegramClient(telegramId).GetFromJsonAsync<MeDto>("/api/auth/me", JsonOpts);
        me!.Email.Should().Be(email);
        me.HasTelegram.Should().BeTrue();
        me.HasPassword.Should().BeTrue("новый аккаунт должен получить сгенерированный сервером временный пароль, иначе вход в PWA станет невозможен");

        // Сервер сам сгенерировал пароль и прислал его на почту — извлекаем его из письма и
        // проверяем, что им реально можно войти (а не только что письмо ушло).
        var temporaryPassword = factory.Emails.LastTemporaryPasswordFor(email);
        temporaryPassword.Should().NotBeNullOrEmpty();
        (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = temporaryPassword }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Revoke_FromPwaSession_MakesNextTelegramRequestUnauthorized()
    {
        var (email, pwaClient, _) = await RegisterPwaUserWithFamilyAsync();
        var telegramId = FreshTelegramId();

        (await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/telegram/send-code", new { email, initData = BuildSignedInitData(telegramId) }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var code = factory.Emails.LastCodeFor(email);
        (await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/telegram/bind", new { email, code, initData = BuildSignedInitData(telegramId) }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var telegramClient = TelegramClient(telegramId);
        (await telegramClient.GetAsync("/api/families")).StatusCode.Should().Be(HttpStatusCode.OK, "до отвязки доступ рабочий");

        (await pwaClient.PostAsync("/api/auth/telegram/revoke", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Telegram — без сессии/токена: отвязка мгновенно эффективна на самом первом же
        // следующем запросе (initData валиден, но lookup по TelegramId больше ничего не находит).
        (await TelegramClient(telegramId).GetAsync("/api/families")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // Регрессия на самоблокировку (аудит 2026-08-02, находка [01]): /revoke раньше не проверял
    // на бэкенде наличие пароля — гейт был только в UI. Telegram-only пользователь (нет Email/
    // PasswordHash) мог необратимо обнулить свой единственный способ входа.
    [Fact]
    public async Task Revoke_TelegramOnlyAccountWithoutPassword_Returns409_AndAccessStaysIntact()
    {
        var telegramId = FreshTelegramId();
        // Dev-схема (X-Dev-TelegramId) создаёт пользователя тем же путём, что и штатный Telegram
        // Mini App логин без предварительной email-привязки: TelegramId есть, Email/PasswordHash — нет.
        var telegramOnlyClient = factory.CreateClientAs(telegramId);
        (await telegramOnlyClient.GetAsync("/api/families")).StatusCode.Should().Be(HttpStatusCode.OK, "аккаунт создан и рабочий");

        var revokeResponse = await telegramOnlyClient.PostAsync("/api/auth/telegram/revoke", null);

        revokeResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await revokeResponse.Content.ReadFromJsonAsync<ErrorCodeDto>(JsonOpts);
        body!.Code.Should().Be("password_required");

        // TelegramId не тронут — доступ по-прежнему рабочий.
        (await factory.CreateClientAs(telegramId).GetAsync("/api/families")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private record ErrorCodeDto(string Code);
    private record BoundDto(bool Bound);
    private record CreatedFamilyDto(Guid Id);
    private record FamilyDto(Guid Id, string Name);
    private record MeDto(Guid UserId, string DisplayName, string Provider, string? Email, bool HasTelegram, bool HasPassword);
}
