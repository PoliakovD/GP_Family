using FamilyHub.Contracts.Events;
using FamilyHub.TelegramBot.Configuration;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace FamilyHub.TelegramBot.Messaging;

/// <summary>
/// Единственный потребитель бота — принимает уже готовое к отправке сообщение
/// (TelegramOutboundPublisher на стороне Api сделал дедуп/фильтр по предпочтениям) и шлёт его
/// через ITelegramBotClient. Строит ту же кнопку "Открыть FamilyHub", что раньше строил
/// TelegramNotificationSender.
///
/// Осознанное изменение поведения относительно прежнего TelegramNotificationSender (который
/// проглатывал ЛЮБУЮ ошибку отправки, т.к. крутился в батч-цикле ReminderScanJob и не мог уронить
/// соседей): здесь проглатывание = молчаливая потеря сообщения. Транзиентные ошибки (сеть,
/// таймаут — например, туннель временно лёг) пробрасываем, чтобы сработал ретрай эндпоинта
/// (UseMessageRetry) и в конце dead-letter; перманентные ошибки Bot API (403 — бот заблокирован
/// пользователем, 400 — чат не найден) проглатываем и логируем — ретраить их бессмысленно.
/// At-least-once допускает дубли Telegram-сообщений при ретрае — не критично для оповещений.
/// </summary>
public class TelegramOutboundConsumer(
    ITelegramBotClient bot, IOptions<BotOptions> options, ILogger<TelegramOutboundConsumer> logger)
    : IConsumer<TelegramMessageRequestedEvent>
{
    public async Task Consume(ConsumeContext<TelegramMessageRequestedEvent> context)
    {
        var message = context.Message;
        var miniAppUrl = options.Value.MiniAppUrl;
        ReplyMarkup? markup = message.WithMiniAppButton && !string.IsNullOrWhiteSpace(miniAppUrl)
            ? new InlineKeyboardMarkup(InlineKeyboardButton.WithWebApp("Открыть FamilyHub", new WebAppInfo(miniAppUrl)))
            : null;

        try
        {
            await bot.SendMessage(message.ChatId, message.Text, replyMarkup: markup, cancellationToken: context.CancellationToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode is 400 or 403)
        {
            // Чат не найден / бот заблокирован пользователем — перманентно, ретрай не поможет.
            logger.LogWarning(ex,
                "Telegram отклонил сообщение для чата {ChatId} (DedupKey={DedupKey}) кодом {ErrorCode} — не ретраим.",
                message.ChatId, message.DedupKey, ex.ErrorCode);
        }
    }
}
