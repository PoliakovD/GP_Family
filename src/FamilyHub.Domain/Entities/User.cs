namespace FamilyHub.Domain.Entities;

/// <summary>
/// Пользователь. Два способа входа (этап 2 п.2.4): Telegram Mini App (TelegramId) и
/// PWA (Email + пароль). Хотя бы один из идентификаторов заполнен; аккаунт может иметь оба
/// (привязка email из Telegram-сессии).
/// </summary>
public class User
{
    public Guid Id { get; set; }

    /// <summary>Telegram user id; null — пользователь зарегистрирован только через PWA.</summary>
    public long? TelegramId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Видимый username аккаунта (уникальный, формат — см. UsernameRules), задаётся при
    /// PWA-регистрации. Для Telegram-пользователей при первом входе копируется из TgUsername,
    /// если тот свободен и валиден по формату — иначе остаётся null (не назначается автоматически
    /// повторно). НЕ путать с <see cref="TgUsername"/>, который не уникален.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>Telegram @username (без '@'), зеркалится и обновляется при каждом TG-входе. Не уникален.</summary>
    public string? TgUsername { get; set; }

    /// <summary>Email для PWA-входа (нормализован в lowercase); null — вход только через Telegram.</summary>
    public string? Email { get; set; }

    /// <summary>PBKDF2-хеш пароля PWA-входа (см. PasswordHasher); null — пароль не установлен.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>Подряд неудачные попытки входа — основа lockout-защиты от брутфорса.</summary>
    public int FailedLoginAttempts { get; set; }

    /// <summary>До этого момента PWA-вход заблокирован (после серии неудачных попыток входа).</summary>
    public DateTime? LockedUntil { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<FamilyMember> Memberships { get; set; } = [];
}
