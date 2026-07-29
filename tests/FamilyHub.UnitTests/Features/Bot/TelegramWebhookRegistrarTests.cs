using FamilyHub.Api.Features.Bot;
using FamilyHub.Infrastructure.Telegram;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

namespace FamilyHub.UnitTests.Features.Bot;

/// <summary>
/// Регрессия на баг: инлайн-кнопки "Привязать"/"Отмена" (TelegramUpdateHandler.
/// HandleCallbackQueryAsync) молчали, т.к. setWebhook регистрировался БЕЗ CallbackQuery в
/// allowedUpdates — Telegram фильтрует доставку апдейтов на своей стороне, поэтому такие
/// апдейты вообще не доходили до /bot/webhook. Прикладная логика была написана и покрыта
/// тестами (TelegramUpdateHandlerTests, TelegramLinkFlowTests) корректно с самого начала —
/// они синтезируют Update и шлют его напрямую, минуя реальную доставку от Telegram, поэтому
/// этот дефект ими не ловился. Этот тест проверяет именно payload setWebhook.
/// </summary>
public class TelegramWebhookRegistrarTests
{
    private readonly ITelegramBotClient _bot = Substitute.For<ITelegramBotClient>();
    private readonly ILogger<TelegramWebhookRegistrar> _logger = Substitute.For<ILogger<TelegramWebhookRegistrar>>();

    private TelegramWebhookRegistrar CreateSut(TelegramOptions opts)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_bot);
        return new TelegramWebhookRegistrar(services.BuildServiceProvider(), Options.Create(opts), _logger);
    }

    [Fact]
    public async Task StartAsync_RegistersWebhook_WithMessageAndCallbackQueryAllowed()
    {
        var opts = new TelegramOptions { BotToken = "dummy-token", WebhookUrl = "https://example.test/bot/webhook" };
        _bot.SendRequest(Arg.Any<SetWebhookRequest>(), Arg.Any<CancellationToken>()).Returns(true);

        await CreateSut(opts).StartAsync(CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<SetWebhookRequest>(r =>
                r.AllowedUpdates != null &&
                r.AllowedUpdates.Contains(UpdateType.Message) &&
                r.AllowedUpdates.Contains(UpdateType.CallbackQuery)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_WebhookUrlNotConfigured_DoesNotCallBot()
    {
        var opts = new TelegramOptions { BotToken = "dummy-token", WebhookUrl = "" };

        await CreateSut(opts).StartAsync(CancellationToken.None);

        await _bot.DidNotReceive().SendRequest(Arg.Any<SetWebhookRequest>(), Arg.Any<CancellationToken>());
    }
}
