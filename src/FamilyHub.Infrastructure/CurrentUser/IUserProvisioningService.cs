namespace FamilyHub.Infrastructure.CurrentUser;

/// <summary>Get-or-create пользователя по TelegramId. Вызывается из auth-хендлеров на каждый запрос.</summary>
public interface IUserProvisioningService
{
    Task<Guid> GetOrCreateUserIdAsync(long telegramId, string? displayName, string? username = null, CancellationToken ct = default);
}
