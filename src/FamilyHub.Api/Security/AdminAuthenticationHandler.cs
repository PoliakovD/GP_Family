using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Security;

/// <summary>
/// Схема аутентификации админ-панели (ADR-0009) — читает httpOnly-cookie
/// <see cref="AdminCookieNames.Session"/>, выставленную POST /api/admin/session после проверки
/// логина/пароля (AdminBasicAuth.IsAuthorized, тот же constant-time compare, что у Hangfire/
/// Swagger-гейта). Никогда не участвует в AuthSchemes.Smart-селекторе — подключается ТОЛЬКО
/// явной политикой "PlatformAdmin" на группе /api/admin (см. Program.cs), поэтому обычные PWA/
/// Telegram-запросы её не видят вовсе.
/// </summary>
public class AdminAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IDataProtectionProvider dataProtection)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(AdminCookieNames.Session, out var token) || string.IsNullOrEmpty(token))
            return Task.FromResult(AuthenticateResult.Fail("Отсутствует cookie сессии админ-панели."));

        if (!AdminSessionCookie.Validate(dataProtection, token))
        {
            Logger.LogWarning("Аутентификация админ-панели отклонена: cookie недействительна/просрочена ({Path})", Request.Path);
            return Task.FromResult(AuthenticateResult.Fail("Сессия админ-панели недействительна или истекла."));
        }

        // Единственный логин на всю панель — identity без UserId, только сам факт "это админ".
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
