namespace FamilyHub.Domain.Entities;

/// <summary>
/// Одноразовый код для привязки Telegram-аккаунта к существующему email/PWA-аккаунту
/// с подтверждением с другой стороны (через бота). Генерируется в настройках веб-аккаунта,
/// предъявляется боту через deep-link (t.me/bot?start=link___&lt;code&gt;), подтверждается
/// нажатием inline-кнопки в Telegram. Хранится только SHA-256-хеш кода — сам код нигде
/// не хранится, как и в EmailVerificationCode.
/// </summary>
public class TelegramLinkCode
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 (hex) 32-символьного случайного кода.</summary>
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>Веб/email-аккаунт, к которому будет привязан Telegram (переживает merge).</summary>
    public Guid UserId { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>Момент подтверждения; непустое значение делает код недействительным.</summary>
    public DateTime? ConsumedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}
