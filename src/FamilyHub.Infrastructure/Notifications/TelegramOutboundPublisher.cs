using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Messaging;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Infrastructure.Notifications;

/// <summary>
/// Доставка оповещений в Telegram — заменяет прежний TelegramNotificationSender (этап 4 п.12)
/// после выноса бота в отдельный процесс (FamilyHub.TelegramBot, у которого нет доступа к БД).
/// Резолв TelegramId и логика "нет TG-канала — молча выходим" — те же, что были в
/// TelegramNotificationSender; изменилось только КАК доставляется сообщение: вместо прямого
/// ITelegramBotClient.SendMessage публикуем TelegramMessageRequestedEvent через outbox-aware
/// IDomainEventPublisher — публикация становится атомарной с записью Notification.SentAt
/// (NotificationSendingService.TrySendAsync делает SaveChangesAsync сразу после этого вызова),
/// что строго надёжнее прежнего "запостили в Telegram, потом сохранили SentAt отдельно".
/// ReminderScanJob не меняется: он зависит только от абстракции INotificationSender.
/// </summary>
public class TelegramOutboundPublisher(
    IDomainEventPublisher publisher,
    AppDbContext db,
    ILogger<TelegramOutboundPublisher> logger) : INotificationSender
{
    public NotificationChannel Channel => NotificationChannel.Telegram;

    public async Task SendAsync(Notification notification, CancellationToken ct = default)
    {
        var telegramId = await db.Users.AsNoTracking()
            .Where(u => u.Id == notification.UserId)
            .Select(u => u.TelegramId)
            .FirstOrDefaultAsync(ct);

        if (telegramId is null or 0)
        {
            // PWA-only пользователь (без Telegram) или пользователь не найден — TG-канала нет.
            logger.LogDebug(
                "Нет TelegramId у пользователя {UserId} — оповещение {NotificationId} не будет опубликовано для TG.",
                notification.UserId, notification.Id);
            return;
        }

        // WithMiniAppButton всегда true: строит ли бот кнопку на самом деле, зависит от того,
        // сконфигурирован ли у НЕГО Telegram:MiniAppUrl (см. TelegramOutboundConsumer в
        // FamilyHub.TelegramBot) — у Api этого ключа больше нет, решение переехало вместе с ботом.
        await publisher.PublishAsync(
            new TelegramMessageRequestedEvent(
                telegramId.Value,
                $"{notification.Title}\n\n{notification.Body}",
                WithMiniAppButton: true,
                notification.DedupKey),
            ct);
    }
}
