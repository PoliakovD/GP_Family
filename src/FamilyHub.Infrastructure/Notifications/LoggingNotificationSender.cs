using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Infrastructure.Notifications;

/// <summary>
/// Временная реализация INotificationSender — просто пишет в лог. Заменяется на реальный
/// Telegram-sender позже (после этапа 4 п.12), без изменений в ReminderScanJob.
/// </summary>
public class LoggingNotificationSender(ILogger<LoggingNotificationSender> logger) : INotificationSender
{
    /// <summary>Дев-заглушка не фильтруется предпочтениями (см. NotificationSendingService.TrySendAsync).</summary>
    public NotificationChannel Channel => NotificationChannel.Log;

    public Task SendAsync(Notification notification, CancellationToken ct = default)
    {
        logger.LogInformation(
            "Оповещение {Type} для пользователя {UserId}: {Title} — {Body}",
            notification.Type, notification.UserId, notification.Title, notification.Body);

        return Task.CompletedTask;
    }
}
