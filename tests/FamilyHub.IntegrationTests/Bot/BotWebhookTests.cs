using System.Net;
using System.Net.Http.Json;
using FamilyHub.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

namespace FamilyHub.IntegrationTests.Bot;

[CollectionDefinition(Name)]
public class BotWebhookTestCollection : ICollectionFixture<BotWebhookWebFactory>
{
    public const string Name = "FamilyHub bot webhook tests";
}

/// <summary>
/// Сквозной HTTP-тест вебхука: реальная проверка секрета + реальный JSON-разбор Update через
/// Telegram.Bot.JsonBotAPI.Options + реальный TelegramUpdateHandler/InviteService/UserProvisioning
/// на настоящем Postgres. Единственная подмена — ITelegramBotClient (см. BotWebhookWebFactory),
/// чтобы ответы хендлера не уходили в реальный Telegram.
/// </summary>
[Collection(BotWebhookTestCollection.Name)]
public class BotWebhookTests(BotWebhookWebFactory factory)
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
        var client = factory.CreateClient();
        factory.BotClient.ClearReceivedCalls();

        var response = await client.SendAsync(BuildRequest(StartUpdate(901), secret: null));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await factory.BotClient.DidNotReceive().SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WrongSecret_Returns401()
    {
        var client = factory.CreateClient();

        var response = await client.SendAsync(BuildRequest(StartUpdate(902), secret: "not-the-real-secret"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CorrectSecret_StartCommand_Returns200_AndRepliesViaBotClient_AndProvisionsUser()
    {
        var client = factory.CreateClient();
        const long telegramId = 903;

        var response = await client.SendAsync(BuildRequest(StartUpdate(telegramId), secret: BotWebhookWebFactory.WebhookSecret));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await factory.BotClient.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.ChatId == telegramId),
            Arg.Any<CancellationToken>());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Users.Should().Contain(u => u.TelegramId == telegramId);
    }
}
