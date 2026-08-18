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

        try
        {
            await bot.SetWebhook(
                url: opts.WebhookUrl,
                secretToken: string.IsNullOrEmpty(opts.WebhookSecret) ? null : opts.WebhookSecret,
                // CallbackQuery обязателен: инлайн-кнопки "Привязать"/"Отмена" (TelegramUpdateHandler.
                // HandleCallbackQueryAsync) без него молчат — Telegram фильтрует доставку по этому
                // списку НА СВОЕЙ стороне, /bot/webhook такие апдейты вообще не получает.
                allowedUpdates: new[] { UpdateType.Message, UpdateType.CallbackQuery },
                cancellationToken: cancellationToken);

            if (!string.IsNullOrWhiteSpace(opts.MiniAppUrl))
            {
                await bot.SetChatMenuButton(
                    menuButton: new MenuButtonWebApp { Text = "FamilyHub", WebApp = new WebAppInfo(opts.MiniAppUrl) },
                    cancellationToken: cancellationToken);
            }

            logger.LogInformation("Telegram webhook зарегистрирован: {Url}", opts.WebhookUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Недоступность Telegram API (невалидный BotToken, нет egress с VPS, таймаут) не должна
            // валить весь хост — остальной API (HTTP-приём /bot/webhook, всё остальное) не зависит
            // от того, успела ли зарегистрироваться сама подписка на вебхук у Telegram.
            logger.LogError(ex, "Не удалось зарегистрировать Telegram webhook — API продолжает запуск без него.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
