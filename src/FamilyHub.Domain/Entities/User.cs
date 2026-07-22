namespace FamilyHub.Domain.Entities;

/// <summary>
/// Пользователь. Два способа входа (этап 2 п.2.4): Telegram Mini App (TelegramId) и
/// PWA (Email + PIN). Хотя бы один из идентификаторов заполнен; аккаунт может иметь оба
/// (привязка email из Telegram-сессии).
/// </summary>
public class User
{
    public Guid Id { get; set; }

    /// <summary>Telegram user id; null — пользователь зарегистрирован только через PWA.</summary>
    public long? TelegramId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Telegram @username (без '@'), может отсутствовать у пользователя.</summary>
    public string? Username { get; set; }

    /// <summary>Email для PWA-входа (нормализован в lowercase); null — вход только через Telegram.</summary>
    public string? Email { get; set; }

    /// <summary>PBKDF2-хеш PIN-кода PWA-входа (см. PinHasher); null — PIN не установлен.</summary>
    public string? PinHash { get; set; }

    /// <summary>Подряд неудачные попытки PIN — основа lockout-защиты от брутфорса.</summary>
    public int FailedPinAttempts { get; set; }

    /// <summary>До этого момента PWA-вход заблокирован (после серии неудачных PIN).</summary>
    public DateTime? LockedUntil { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<FamilyMember> Memberships { get; set; } = [];
}
