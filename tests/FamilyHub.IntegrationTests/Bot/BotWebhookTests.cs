using System.Net;
using System.Net.Http.Json;
using FamilyHub.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

namespace FamilyHub.IntegrationTests.Bot;

/// <summary>
/// Сквозной HTTP-тест вебхука после выноса бота (ADR-0008): реальная проверка секрета + реальный
/// JSON-разбор Update через Telegram.Bot.JsonBotAPI.Options на хосте FamilyHub.TelegramBot,
/// который затем реальным HTTP-вызовом (через TestServer.CreateHandler(), см.
/// BotIntegrationFixture) достигает /internal/bot/* на хосте FamilyHub.Api — InviteService/
/// UserProvisioning на настоящем Postgres, как и раньше, просто теперь через сетевую границу
/// между двумя процессами вместо in-process вызова. Единственная подмена — ITelegramBotClient
/// (внутри бота), чтобы ответы хендлера не уходили в реальный Telegram.
/// </summary>
[Collection(BotIntegrationCollection.Name)]
public class BotWebhookTests(BotIntegrationFixture fixture)
{
    private static Update StartUpdate(long fromId, string? argument = null) => new()
    {
        Message = new Message
        {
            Text = argument is null ? "/start" : $"/start {argument}",
            Chat = new Chat { Id = fromId, Type = ChatType.Private },
            From = new User { Id = fromId, FirstName = "Webhook Tester" },
        },
    };

    private HttpRequestMessage BuildRequest(Update update, string? secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/bot/webhook")
        {
            Content = JsonContent.Create(update, options: Telegram.Bot.JsonBotAPI.Options),
        };
        if (secret is not null)
            request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", secret);
        return request;
    }

    [Fact]
    public async Task MissingSecretHeader_Returns401_AndHandlerNeverRuns()
    {
        var client = fixture.CreateBotWebhookClient();
        fixture.BotClient.ClearReceivedCalls();

        var response = await client.SendAsync(BuildRequest(StartUpdate(901), secret: null));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await fixture.BotClient.DidNotReceive().SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WrongSecret_Returns401()
    {
        var client = fixture.CreateBotWebhookClient();

        var response = await client.SendAsync(BuildRequest(StartUpdate(902), secret: "not-the-real-secret"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CorrectSecret_StartCommand_Returns200_AndRepliesViaBotClient_DoesNotProvisionUnboundUser()
    {
        // Lookup-only: бот никогда не создаёт "голого" Telegram-only пользователя без
        // email-привязки (email — единственный якорь identity, см. TelegramMiniAppAuthenticationHandler).
        var client = fixture.CreateBotWebhookClient();
        const long telegramId = 903;

        var response = await client.SendAsync(BuildRequest(StartUpdate(telegramId), secret: BotIntegrationFixture.WebhookSecret));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.BotClient.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.ChatId == telegramId),
            Arg.Any<CancellationToken>());

        using var scope = fixture.ApiServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Users.Should().NotContain(u => u.TelegramId == telegramId);
    }
}
