using System.Net;
using System.Net.Http.Json;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;
using DomainUser = FamilyHub.Domain.Entities.User;

namespace FamilyHub.IntegrationTests.Bot;

/// <summary>
/// Сквозной сценарий привязки Telegram к веб/email-аккаунту "с подтверждением с другой
/// стороны" (см. план "UI/UX + Auth Rework"): POST /api/auth/link-telegram/start (хост Api) выдаёт
/// код → /bot/webhook (хост TelegramBot) "/start link___&lt;code&gt;" показывает confirm-клавиатуру
/// → webhook с CallbackQuery подтверждает — напрямую (нет существующего TG-аккаунта) или через
/// AccountMergeService (есть). После выноса бота (ADR-0008) эти два шага идут на РАЗНЫЕ хосты
/// (см. BotIntegrationFixture) — /api/* на fixture.CreateApiClient(), /bot/webhook на
/// fixture.CreateBotWebhookClient(); сценарий и инварианты не изменились, только транспорт.
/// </summary>
[Collection(BotIntegrationCollection.Name)]
public class TelegramLinkFlowTests(BotIntegrationFixture fixture)
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    private async Task<(Guid UserId, HttpClient Client)> SeedPwaUserAsync(string email, string password = "Passw0rd")
    {
        using var scope = fixture.ApiServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new DomainUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = PasswordHasher.Hash(password),
            DisplayName = "Web User",
            CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var client = fixture.CreateApiClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        await CsrfTestHelper.CaptureCsrfTokenAsync(client);

        return (user.Id, client);
    }

    private async Task<string> StartLinkAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/link-telegram/start", new { });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<StartLinkDto>(JsonOpts);
        body!.Code.Should().NotBeNullOrEmpty();
        body.DeepLink.Should().Contain($"link___{body.Code}");
        return body.Code;
    }

    private static Update StartLinkUpdate(long telegramId, string code) => new()
    {
        Message = new Message
        {
            Text = $"/start link___{code}",
            Chat = new Chat { Id = telegramId, Type = ChatType.Private },
            From = new User { Id = telegramId, FirstName = "Linker" },
        },
    };

    private static Update CallbackUpdate(long telegramId, int messageId, string code) => new()
    {
        CallbackQuery = new CallbackQuery
        {
            Id = $"cb-{Guid.NewGuid():N}",
            From = new User { Id = telegramId, FirstName = "Linker" },
            Message = new Message { Id = messageId, Chat = new Chat { Id = telegramId, Type = ChatType.Private } },
            Data = $"link:{code}",
        },
    };

    private async Task PostWebhookAsync(Update update)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/bot/webhook")
        {
            Content = JsonContent.Create(update, options: Telegram.Bot.JsonBotAPI.Options),
        };
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", BotIntegrationFixture.WebhookSecret);
        (await fixture.CreateBotWebhookClient().SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task StartCommand_WithLinkCode_ShowsConfirmKeyboard()
    {
        var (_, client) = await SeedPwaUserAsync($"peek-{Guid.NewGuid():N}@example.com");
        var code = await StartLinkAsync(client);
        fixture.BotClient.ClearReceivedCalls();

        await PostWebhookAsync(StartLinkUpdate(TelegramId(), code));

        await fixture.BotClient.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.Text.Contains("Привязать") && r.ReplyMarkup is InlineKeyboardMarkup),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CallbackConfirm_NoExistingTelegramUser_LinksDirectly()
    {
        var email = $"direct-{Guid.NewGuid():N}@example.com";
        var (userId, client) = await SeedPwaUserAsync(email);
        var code = await StartLinkAsync(client);
        var telegramId = TelegramId();

        await PostWebhookAsync(StartLinkUpdate(telegramId, code));
        await PostWebhookAsync(CallbackUpdate(telegramId, messageId: 1, code));

        using var scope = fixture.ApiServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.TelegramId.Should().Be(telegramId);

        var me = await (await client.GetAsync("/api/auth/me")).Content.ReadFromJsonAsync<MeDto>(JsonOpts);
        me!.HasTelegram.Should().BeTrue();
    }

    [Fact]
    public async Task CallbackConfirm_ExistingSeparateTelegramUser_MergesAccounts()
    {
        var email = $"merge-{Guid.NewGuid():N}@example.com";
        var (userId, client) = await SeedPwaUserAsync(email);
        var telegramId = TelegramId();

        // Отдельная запись User с этим TelegramId, заведённая ДО перехода на lookup-only
        // бота (тот больше не создаёт голых Telegram-аккаунтов сам — см.
        // TelegramUpdateHandler/[[auth-architecture-email-anchor]]). Такие "осиротевшие"
        // записи могли остаться в проде с прошлого дизайна — этот сценарий проверяет,
        // что путь слияния через AccountMergeService всё ещё их подхватывает.
        using (var scope = fixture.ApiServices.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new DomainUser
            {
                Id = Guid.NewGuid(),
                TelegramId = telegramId,
                DisplayName = "PreExisting",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var code = await StartLinkAsync(client);
        await PostWebhookAsync(StartLinkUpdate(telegramId, code));
        await PostWebhookAsync(CallbackUpdate(telegramId, messageId: 2, code));

        using var verifyScope = fixture.ApiServices.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = await verifyDb.Users.Where(u => u.TelegramId == telegramId || u.Id == userId).ToListAsync();
        users.Should().ContainSingle("Telegram-only аккаунт должен слиться с веб-аккаунтом, а не остаться отдельной строкой");
        users.Single().Id.Should().Be(userId, "выживает веб/email-аккаунт");
        users.Single().TelegramId.Should().Be(telegramId);
    }

    [Fact]
    public async Task CallbackConfirm_CalledTwice_SecondAttemptReportsInvalidCode()
    {
        var (_, client) = await SeedPwaUserAsync($"twice-{Guid.NewGuid():N}@example.com");
        var code = await StartLinkAsync(client);
        var telegramId = TelegramId();
        await PostWebhookAsync(StartLinkUpdate(telegramId, code));

        await PostWebhookAsync(CallbackUpdate(telegramId, messageId: 3, code));
        fixture.BotClient.ClearReceivedCalls();
        await PostWebhookAsync(CallbackUpdate(telegramId, messageId: 3, code));

        await fixture.BotClient.Received(1).SendRequest(
            Arg.Is<EditMessageTextRequest>(r => r.Text.Contains("недействителен")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartLinkTelegram_UserAlreadyLinked_Returns409()
    {
        var (_, client) = await SeedPwaUserAsync($"already-{Guid.NewGuid():N}@example.com");
        var code = await StartLinkAsync(client);
        var telegramId = TelegramId();
        await PostWebhookAsync(StartLinkUpdate(telegramId, code));
        await PostWebhookAsync(CallbackUpdate(telegramId, messageId: 4, code));

        var response = await client.PostAsJsonAsync("/api/auth/link-telegram/start", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private static long TelegramId() => Random.Shared.NextInt64(1_000_000_000, 9_000_000_000);

    private record StartLinkDto(string Code, string DeepLink, DateTime ExpiresAt);
    private record MeDto(Guid UserId, string DisplayName, string Provider, string? Email, bool HasTelegram, bool HasPassword);
}
