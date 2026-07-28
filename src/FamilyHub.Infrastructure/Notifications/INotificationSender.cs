using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;

namespace FamilyHub.Infrastructure.Notifications;

/// <summary>
/// Абстракция доставки уже созданного оповещения (mirrors IFileStorage pattern). Создание
/// записи в БД (ReminderScanJob) полностью отделено от способа доставки — сейчас это
/// log-заглушка, позже сюда встанет реальный Telegram-sender без изменений в вызывающем коде.
/// </summary>
public interface INotificationSender
{
    /// <summary>Канал, которым фильтруются пользовательские предпочтения
    /// (см. NotificationSendingService.TrySendAsync + UserNotificationPreference).</summary>
    NotificationChannel Channel { get; }

    Task SendAsync(Notification notification, CancellationToken ct = default);
}
