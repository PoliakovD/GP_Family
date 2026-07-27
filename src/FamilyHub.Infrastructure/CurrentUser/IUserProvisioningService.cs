namespace FamilyHub.Infrastructure.CurrentUser;

/// <summary>Get-or-create пользователя по TelegramId. Вызывается из auth-хендлеров на каждый запрос.</summary>
public interface IUserProvisioningService
{
    /// <summary>
    /// Только Development (DevAuthenticationHandler, X-Dev-TelegramId) — auto-create для
    /// тестового удобства. Реальный Telegram Mini App (TelegramMiniAppAuthenticationHandler)
    /// использует <see cref="GetUserIdByTelegramIdAsync"/> (lookup-only): TelegramId без
    /// email-подтверждённого User не должен молча создавать новый аккаунт — это ровно то,
    /// что приводило к разделённым (Telegram-only) личностям, требующим слияния позже.
    /// </summary>
    Task<Guid> GetOrCreateUserIdAsync(long telegramId, string? displayName, string? username = null, CancellationToken ct = default);

    /// <summary>Только чтение — null, если этот TelegramId ещё не привязан ни к одному User.</summary>
    Task<Guid?> GetUserIdByTelegramIdAsync(long telegramId, CancellationToken ct = default);
}
