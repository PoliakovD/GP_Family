using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

namespace FamilyHub.Infrastructure.Auth.Jwt;

/// <summary>
/// Установка/очистка httpOnly-cookie PWA-сессии. Общая точка для всех эндпоинтов, которые
/// выпускают (auth login/register/reset-password/refresh) или обязаны закрыть сессию (auth logout,
/// account delete) — раньше закрытие делалось через <c>Results.SignOut</c>, что работало для
/// cookie-схемы, но ломается для JwtBearer (не реализует IAuthenticationSignOutHandler).
/// </summary>
public static class PwaSessionCookieWriter
{
    public static void SetSessionCookies(HttpContext http, IssuedSession session)
    {
        var secure = http.Request.IsHttps; // SameAsRequest: TLS в проде, http локально.

        http.Response.Cookies.Append(PwaCookieNames.AccessToken, session.AccessToken, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = secure,
            Expires = session.AccessTokenExpiresAt,
            Path = "/",
        });
        http.Response.Cookies.Append(PwaCookieNames.RefreshToken, session.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = secure,
            Expires = session.RefreshTokenExpiresAt,
            // Только auth-эндпоинты (/refresh, /logout) — незачем гонять долгоживущий секрет
            // с каждым запросом к API.
            Path = "/api/auth",
        });

        // CSRF-cookie здесь НЕ выставляем: login/register/reset-password/confirm/refresh — все
        // AllowAnonymous, а IAntiforgery.GetAndStoreTokens привязывает токен к аутентифицированной
        // identity ТЕКУЩЕГО запроса (HttpContext.User) — в момент вызова этих эндпоинтов новая
        // cookie ещё не подействовала, запрос всё ещё анонимен, и токен, выпущенный "для
        // анонимного", не пройдёт валидацию на последующих АУТЕНТИФИЦИРОВАННЫХ мутирующих
        // запросах (проверено эмпирически: IsRequestValidAsync отклоняет ровно такую пару).
        // Правильное место для выпуска CSRF-cookie — GET /api/auth/me (см. IssueCsrfCookie ниже):
        // он сам аутентифицирован и его и так вызывает SPA сразу после login/register/reset
        // (см. auth.service.ts) и на каждом старте (app.component.ts).
    }

    /// <summary>
    /// Выставляет публичную (не httpOnly) cookie <see cref="CsrfCookieNames.PublicToken"/> со
    /// значением IAntiforgery.RequestToken — её читает Angular (withXsrfConfiguration) и сама
    /// подставляет в заголовок X-XSRF-TOKEN. GetAndStoreTokens тем же вызовом ставит СВОЙ
    /// приватный httpOnly cookie (см. AddAntiforgery в Program.cs) — оба значения криптографически
    /// связаны, сверяются вместе в CSRF-гейте (Program.cs). Вызывать ТОЛЬКО из аутентифицированного
    /// контекста (см. GET /api/auth/me) — GetAndStoreTokens привязывает токен к identity текущего
    /// запроса, и пара, выпущенная в анонимном контексте, не пройдёт валидацию на аутентифицированном.
    /// </summary>
    public static void IssueCsrfCookie(HttpContext http, IAntiforgery antiforgery, DateTime expiresAt, bool? secure = null)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);
        http.Response.Cookies.Append(CsrfCookieNames.PublicToken, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Lax,
            Secure = secure ?? http.Request.IsHttps,
            Expires = expiresAt,
            Path = "/",
        });
    }

    public static void ClearSessionCookies(HttpContext http)
    {
        http.Response.Cookies.Delete(PwaCookieNames.AccessToken, new CookieOptions { Path = "/" });
        http.Response.Cookies.Delete(PwaCookieNames.RefreshToken, new CookieOptions { Path = "/api/auth" });
        http.Response.Cookies.Delete(CsrfCookieNames.PublicToken, new CookieOptions { Path = "/" });
    }
}
