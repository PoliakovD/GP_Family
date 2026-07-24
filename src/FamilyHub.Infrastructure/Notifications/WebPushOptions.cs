namespace FamilyHub.Infrastructure.Notifications;

/// <summary>
/// Конфигурация Web Push (редизайн навигации, ADR-0004) — реальная доставка PWA-пользователям
/// (Telegram-only канал их не покрывает). Ключи — только из окружения, вне appsettings/БД
/// (тот же принцип, что EncryptionOptions.MasterKey).
/// </summary>
public class WebPushOptions
{
    public const string SectionName = "WebPush";

    /// <summary>Публичный VAPID-ключ (base64url) — раздаётся фронту через GET /api/push/vapid-public-key.</summary>
    public string VapidPublicKey { get; set; } = string.Empty;

    /// <summary>Приватный VAPID-ключ (base64url) — подписывает исходящие push-запросы, наружу не отдаётся.</summary>
    public string VapidPrivateKey { get; set; } = string.Empty;

    /// <summary>"mailto:..." — обязателен по спецификации VAPID (контакт для push-релея при жалобах).</summary>
    public string Subject { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(VapidPublicKey)
        && !string.IsNullOrWhiteSpace(VapidPrivateKey)
        && !string.IsNullOrWhiteSpace(Subject);
}
