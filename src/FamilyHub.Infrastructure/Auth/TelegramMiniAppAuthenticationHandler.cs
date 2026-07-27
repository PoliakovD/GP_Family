using System.Security.Claims;
using System.Text.Encodings.Web;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.CurrentUser;
using FamilyHub.Infrastructure.Telegram;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Auth;

/// <summary>
/// Схема аутентификации Telegram Mini App. initData принимается в заголовке
/// "Authorization: tma &lt;initData&gt;" (рекомендация Telegram) либо в "X-Telegram-Init-Data"
/// как запасной вариант для отладки. Валидация HMAC — ПЕРВЫЙ шаг, до любой бизнес-логики.
///
/// Lookup-only: НЕ создаёт пользователя, если TelegramId ещё не привязан ни к одному User —
/// раньше здесь был get-or-create, что молча плодило "голые" Telegram-аккаунты без email и
/// требовало последующего слияния с PWA-аккаунтом того же человека. Теперь такой TelegramId
/// должен сначала пройти привязку через email+OTP (POST /api/auth/telegram/init → send-code →
/// bind, см. TelegramBindingService) — только после неё запрос сюда пройдёт успешно.
/// </summary>
public class TelegramMiniAppAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ITelegramInitDataValidator validator,
    IUserProvisioningService userProvisioning)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var initData = ExtractInitData();
        if (initData is null)
        {
            Logger.LogWarning("Telegram Mini App аутентификация отклонена: отсутствует initData ({Path})", Request.Path);
            return AuthenticateResult.Fail("Отсутствует Telegram initData.");
        }

        var result = validator.Validate(initData);
        if (result is null)
        {
            Logger.LogWarning("Telegram Mini App аутентификация отклонена: initData не прошла валидацию ({Path})", Request.Path);
            return AuthenticateResult.Fail("Telegram initData не прошла валидацию подписи.");
        }

        var userId = await userProvisioning.GetUserIdByTelegramIdAsync(result.TelegramId, Context.RequestAborted);
        if (userId is null)
        {
            Logger.LogInformation(
                "Telegram Mini App аутентификация отклонена: TelegramId={TelegramId} не привязан ни к одному аккаунту " +
                "(требуется /api/auth/telegram/init → bind)", result.TelegramId);
            return AuthenticateResult.Fail("TelegramId не привязан к аккаунту.");
        }

        Logger.LogDebug("Telegram Mini App аутентификация: TelegramId={TelegramId} -> UserId={UserId}", result.TelegramId, userId);

        var claims = new[]
        {
            new Claim(FamilyHubClaimTypes.UserId, userId.Value.ToString()),
            new Claim(FamilyHubClaimTypes.TelegramId, result.TelegramId.ToString()),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }

    private string? ExtractInitData()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        const string prefix = "tma ";
        if (authHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return authHeader[prefix.Length..];

        var fallback = Request.Headers["X-Telegram-Init-Data"].ToString();
        return string.IsNullOrEmpty(fallback) ? null : fallback;
    }
}
