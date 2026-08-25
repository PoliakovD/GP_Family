using FamilyHub.Contracts.BotApi;

namespace FamilyHub.TelegramBot.Api;

/// <summary>
/// Клиент /internal/bot/* на FamilyHub.Api — заменяет прямые in-process вызовы
/// IUserProvisioningService/InviteService/TelegramLinkService, которых у бота больше нет
/// (нет доступа к БД). Бросает FamilyHubApiUnavailableException на сетевые ошибки/не-2xx.
/// </summary>
public interface IFamilyHubApiClient
{
    Task<ResolveUserResponse> ResolveUserAsync(long telegramId, CancellationToken ct);

    Task<RedeemInviteResponse> RedeemInviteAsync(string code, long telegramId, CancellationToken ct);

    Task<PeekLinkResponse> PeekTelegramLinkAsync(string code, CancellationToken ct);

    Task<ConfirmLinkResponse> ConfirmTelegramLinkAsync(
        string code, long telegramId, string? username, CancellationToken ct);
}
