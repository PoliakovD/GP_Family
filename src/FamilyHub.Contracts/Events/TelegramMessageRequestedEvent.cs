namespace FamilyHub.Contracts.Events;

/// <summary>
/// Готовое к отправке сообщение в Telegram — публикуется FamilyHub.Api (TelegramOutboundPublisher,
/// после дедупа/проверки предпочтений в NotificationSendingService) и потребляется
/// FamilyHub.TelegramBot (TelegramOutboundConsumer), у которого нет доступа к БД и который поэтому
/// не может сам резолвить получателя/предпочтения — только отправить готовый текст в готовый чат.
/// DedupKey — тот же, что у Notification.DedupKey, для опциональной дедупликации на стороне бота
/// (at-least-once доставка топика).
/// </summary>
public record TelegramMessageRequestedEvent(long ChatId, string Text, bool WithMiniAppButton, string DedupKey);
