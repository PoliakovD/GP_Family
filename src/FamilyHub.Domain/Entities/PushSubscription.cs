namespace FamilyHub.Domain.Entities;

/// <summary>
/// Подписка браузера на Web Push (редизайн навигации, ADR-0004) — endpoint push-релея браузера
/// (FCM/Mozilla/Apple) + ключи шифрования payload одного устройства. Endpoint/P256dh/Auth —
/// credential-подобные данные устройства, шифруются at-rest тем же стандартом, что и
/// Birthday.PersonName (ADR-0002). Endpoint при этом ещё и уникален по устройству — поскольку
/// AES-GCM даёт разный шифротекст на одинаковый plaintext (случайный nonce), для upsert/lookup
/// без расшифровки нужен отдельный детерминированный хеш (тот же приём, что CodeHash в
/// TelegramLinkCode/EmailVerificationCode).
/// </summary>
public class PushSubscription
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>SHA-256 (hex) от Endpoint — уникальный индекс, для поиска/upsert без расшифровки.</summary>
    public string EndpointHash { get; set; } = string.Empty;

    [Encrypted]
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Публичный ключ клиента (base64url) для шифрования payload (RFC8291).</summary>
    [Encrypted]
    public string P256dh { get; set; } = string.Empty;

    /// <summary>Auth secret клиента (base64url) для шифрования payload (RFC8291).</summary>
    [Encrypted]
    public string Auth { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>Обновляется при каждой повторной подписке того же устройства (upsert) — не при отправке.</summary>
    public DateTime LastUsedAt { get; set; }
}
