using System.Security.Claims;
using System.Text.Encodings.Web;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.CurrentUser;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Auth;

/// <summary>
/// Dev-заглушка аутентификации: подменяет реального Telegram-бота для локальной разработки.
/// Регистрируется ТОЛЬКО в Development (см. Program.cs хоста) — никогда в проде, иначе любой
/// подделает TelegramId заголовком и зайдёт в чужую семью.
/// Заголовок: "X-Dev-TelegramId: 123456".
/// </summary>
public class DevAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IUserProvisioningService userProvisioning)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers["X-Dev-TelegramId"].ToString();
        if (!long.TryParse(header, out var telegramId) || telegramId <= 0)
        {
            Logger.LogWarning("Dev-аутентификация отклонена: некорректный заголовок X-Dev-TelegramId={Header}", header);
            return AuthenticateResult.Fail("Отсутствует или некорректен заголовок X-Dev-TelegramId.");
        }

        var userId = await userProvisioning.GetOrCreateUserIdAsync(
            telegramId, displayName: null, username: null, Context.RequestAborted);
        Logger.LogDebug("Dev-аутентификация: TelegramId={TelegramId} -> UserId={UserId}", telegramId, userId);

        var claims = new[]
        {
            new Claim(FamilyHubClaimTypes.UserId, userId.ToString()),
            new Claim(FamilyHubClaimTypes.TelegramId, telegramId.ToString()),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}
