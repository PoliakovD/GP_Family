namespace FamilyHub.Domain.Enums;

/// <summary>Канал доставки оповещения — используется фильтром предпочтений
/// (см. UserNotificationPreference) поверх зарегистрированных INotificationSender.</summary>
public enum NotificationChannel
{
    /// <summary>Дев-заглушка (LoggingNotificationSender) — не фильтруется предпочтениями.</summary>
    Log = 0,
    Telegram = 1,
    WebPush = 2,
}
