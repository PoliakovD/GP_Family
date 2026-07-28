using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Предпочтения ДОСТАВКИ оповещений по типу (вкладка «Настройки → Уведомления»). Управляет
/// только каналом (push/Telegram) — запись в ленте /api/notifications создаётся всегда, это
/// история, а не канал (см. NotificationSendingService.AddIfNewAsync). Хранение разреженное:
/// строка существует только при отклонении от дефолта "всё включено" — отсутствие строки для
/// пары (UserId, Type) равносильно PushEnabled=true, TelegramEnabled=true.
/// </summary>
public class UserNotificationPreference
{
    public Guid UserId { get; set; }

    public NotificationType Type { get; set; }

    public bool PushEnabled { get; set; } = true;

    public bool TelegramEnabled { get; set; } = true;
}
