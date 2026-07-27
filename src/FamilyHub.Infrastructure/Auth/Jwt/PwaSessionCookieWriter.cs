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
    }

    public static void ClearSessionCookies(HttpContext http)
    {
        http.Response.Cookies.Delete(PwaCookieNames.AccessToken, new CookieOptions { Path = "/" });
        http.Response.Cookies.Delete(PwaCookieNames.RefreshToken, new CookieOptions { Path = "/api/auth" });
    }
}
