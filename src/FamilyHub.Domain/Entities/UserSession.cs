using System.ComponentModel.DataAnnotations.Schema;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// PWA-сессия (JWT access + DB-backed refresh). Хранится только SHA-256-хеш refresh-токена —
/// сам токен нигде не хранится, как и в EmailVerificationCode/TelegramLinkCode. Ротация при
/// каждом /api/auth/refresh: старый токен помечается RevokedAt + ReplacedByTokenId, выпускается
/// новый. Повторное предъявление уже заменённого токена — признак кражи (reuse detection):
/// отзывается вся цепочка токенов пользователя.
///
/// Только PWA: у Telegram Mini App нет сессии вообще — initData проверяется заново на каждый
/// запрос (см. TelegramMiniAppAuthenticationHandler), поэтому здесь нет колонки-дискриминатора
/// провайдера — пока существует только один провайдер сессий (email+пароль).
/// </summary>
public class UserSession
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>SHA-256 (hex) случайного refresh-токена.</summary>
    public string RefreshTokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Момент отзыва (logout/rotation/reuse-detection/telegram-unbind); непустое — сессия мертва.</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>При ротации — Id токена, который его заменил (цепочка для reuse detection).</summary>
    public Guid? ReplacedByTokenId { get; set; }

    public string? IpAddress { get; set; }

    /// <summary>User-Agent запроса, которым была создана сессия — для списка "мои устройства".</summary>
    public string? DeviceInfo { get; set; }

    [NotMapped]
    public bool IsRevoked => RevokedAt is not null;
}
