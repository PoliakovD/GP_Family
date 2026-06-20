using FamilyHub.Infrastructure.Telegram;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace FamilyHub.Api.Features.Bot;

/// <summary>
/// При старте API регистрирует Telegram-вебхук (setWebhook) и menu-button Mini App — только
/// если сконфигурированы BotToken и WebhookUrl. Без публичного HTTPS-домена (локальный dev)
/// просто пропускает регистрацию, ничего не вызывая у Telegram API.
/// </summary>
public class TelegramWebhookRegistrar(
    IServiceProvider services,
    IOptions<TelegramOptions> options,
    ILogger<TelegramWebhookRegistrar> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var bot = services.GetService<ITelegramBotClient>();
        var opts = options.Value;

        if (bot is null || string.IsNullOrWhiteSpace(opts.WebhookUrl))
        {
            logger.LogInformation("Telegram webhook не зарегистрирован: BotToken или WebhookUrl не заданы.");
            return;
        }

        await bot.SetWebhook(
            url: opts.WebhookUrl,
            secretToken: string.IsNullOrEmpty(opts.WebhookSecret) ? null : opts.WebhookSecret,
            allowedUpdates: new[] { UpdateType.Message },
            cancellationToken: cancellationToken);

        if (!string.IsNullOrWhiteSpace(opts.MiniAppUrl))
        {
            await bot.SetChatMenuButton(
                menuButton: new MenuButtonWebApp { Text = "FamilyHub", WebApp = new WebAppInfo(opts.MiniAppUrl) },
                cancellationToken: cancellationToken);
        }

        logger.LogInformation("Telegram webhook зарегистрирован: {Url}", opts.WebhookUrl);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
