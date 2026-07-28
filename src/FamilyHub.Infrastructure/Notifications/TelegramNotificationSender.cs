using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace FamilyHub.Infrastructure.Notifications;

/// <summary>
/// Реальная доставка оповещений в Telegram (этап 4 п.12) — заменяет LoggingNotificationSender,
/// когда сконфигурирован Telegram:BotToken. ReminderScanJob не меняется: он зависит только
/// от абстракции INotificationSender.
/// </summary>
public class TelegramNotificationSender(
    ITelegramBotClient bot,
    AppDbContext db,
    IOptions<TelegramOptions> options,
    ILogger<TelegramNotificationSender> logger) : INotificationSender
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
                "Нет TelegramId у пользователя {UserId} — оповещение {NotificationId} не доставлено в TG.",
                notification.UserId, notification.Id);
            return;
        }

        var miniAppUrl = options.Value.MiniAppUrl;
        ReplyMarkup? markup = string.IsNullOrWhiteSpace(miniAppUrl)
            ? null
            : new InlineKeyboardMarkup(InlineKeyboardButton.WithWebApp("Открыть FamilyHub", new WebAppInfo(miniAppUrl)));

        try
        {
            await bot.SendMessage(telegramId, $"{notification.Title}\n\n{notification.Body}", replyMarkup: markup, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // Ошибку отправки проглатываем, а не пробрасываем: ReminderScanJob.SendPendingAsync идёт
            // по списку в одном цикле и одним SaveChangesAsync после него — необработанное исключение
            // тут оборвало бы весь батч и откатило бы уже выставленные SentAt у соседних записей.
            logger.LogError(ex,
                "Не удалось отправить оповещение {NotificationId} пользователю {UserId} в Telegram.",
                notification.Id, notification.UserId);
        }
    }
}
