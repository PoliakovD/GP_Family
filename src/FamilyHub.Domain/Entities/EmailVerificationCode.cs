using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Одноразовый код подтверждения email (PWA-регистрация или привязка email к аккаунту).
/// Хранится только SHA-256-хеш кода; попытки ввода ограничены, код одноразовый (ConsumedAt).
/// </summary>
public class EmailVerificationCode
{
    public Guid Id { get; set; }

    /// <summary>Email в lowercase, которому отправлен код.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>SHA-256 (hex) шестизначного кода — сам код нигде не хранится.</summary>
    public string CodeHash { get; set; } = string.Empty;

    public EmailCodePurpose Purpose { get; set; }

    /// <summary>Для Purpose=LinkEmail — аккаунт, к которому привязывается email.</summary>
    public Guid? UserId { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>Число неудачных попыток ввода этого кода (лимит — в PwaAuthService).</summary>
    public int Attempts { get; set; }

    /// <summary>Момент успешного использования; непустое значение делает код недействительным.</summary>
    public DateTime? ConsumedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}
