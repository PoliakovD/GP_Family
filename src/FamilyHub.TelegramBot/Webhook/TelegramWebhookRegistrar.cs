using FamilyHub.TelegramBot.Configuration;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace FamilyHub.TelegramBot.Webhook;

/// <summary>
/// При старте регистрирует Telegram-вебхук (setWebhook) и menu-button Mini App — только если
/// сконфигурированы BotToken и WebhookUrl. Без публичного HTTPS-домена (локальный dev) просто
/// пропускает регистрацию, ничего не вызывая у Telegram API.
/// Ретраит несколько раз с паузой: в отличие от прежнего расположения внутри FamilyHub.Api, здесь
/// первый вызов может случиться раньше, чем поднимется Amnezia WG-туннель (network_mode:
/// service:wg-client, холодный handshake) — без ретрая единственная попытка на старте хоста имела
/// бы неоправданно высокий шанс просто не успеть.
/// </summary>
public class TelegramWebhookRegistrar(
    IServiceProvider services,
    IOptions<BotOptions> options,
    ILogger<TelegramWebhookRegistrar> logger) : IHostedService
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var bot = services.GetService<ITelegramBotClient>();
        var opts = options.Value;

        if (bot is null || string.IsNullOrWhiteSpace(opts.WebhookUrl))
        {
            logger.LogInformation("Telegram webhook не зарегистрирован: BotToken или WebhookUrl не заданы.");
            return;
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
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
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                if (attempt == MaxAttempts)
                {
                    // Недоступность Telegram API (невалидный BotToken, туннель не поднялся, таймаут)
                    // не должна валить весь хост — /bot/webhook продолжает принимать запросы
                    // независимо от того, успела ли зарегистрироваться сама подписка у Telegram.
                    logger.LogError(ex,
                        "Не удалось зарегистрировать Telegram webhook после {Attempts} попыток — хост продолжает запуск без него.",
                        MaxAttempts);
                    return;
                }

                logger.LogWarning(ex,
                    "Попытка {Attempt}/{Max} регистрации Telegram webhook не удалась, повтор через {Delay}s.",
                    attempt, MaxAttempts, RetryDelay.TotalSeconds);
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
