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
            return AuthenticateResult.Fail("Отсутствует Telegram initData.");

        var result = validator.Validate(initData);
        if (result is null)
            return AuthenticateResult.Fail("Telegram initData не прошла валидацию подписи.");

        var userId = await userProvisioning.GetOrCreateUserIdAsync(
            result.TelegramId, result.DisplayName, Context.RequestAborted);

        var claims = new[]
        {
            new Claim(FamilyHubClaimTypes.UserId, userId.ToString()),
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
