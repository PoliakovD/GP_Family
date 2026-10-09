using FamilyHub.Api.Configuration;
using FamilyHub.Api.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Features.Admin;

/// <param name="Totp">Код из приложения-аутентификатора (Admin:TotpSecret); null — если RequireTotp выключен.</param>
public record AdminLoginRequest(string User, string Password, string? Totp = null);

/// <summary>Вход/выход/проверка сессии админ-панели (ADR-0009). См. AdminAuthenticationHandler
/// для проверки cookie на каждый последующий запрос к /api/admin/*.</summary>
public static class AdminSessionEndpoints
{
    public static void MapAdminSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/session").RequireRateLimiting("auth");

        group.MapPost("", async (
            AdminLoginRequest request, HttpContext http, IOptions<AdminOptions> options,
            IDataProtectionProvider dataProtection, AdminLoginThrottle throttle, AdminTotpVerifier totp,
            AdminSessionStore sessions, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("FamilyHub.Admin.Session");
            var ip = http.Connection.RemoteIpAddress?.ToString();

            // Блокировка после серии неудач (аудит security-audit-2026-10, M3) — пока она действует,
            // пароль не проверяется вовсе, иначе перебор продолжался бы, просто получая 429.
            if (throttle.IsLocked())
            {
                logger.LogWarning("Админ-панель: вход отклонён — форма заблокирована после серии неудач (IP {Ip})", ip);
                return Results.Json(new { code = "admin_locked" }, statusCode: StatusCodes.Status429TooManyRequests);
            }

            var admin = options.Value;
            var passwordOk = CredentialComparer.Matches(request.User, request.Password, admin.User, admin.Password);
            // Второй фактор проверяется только при верном пароле (не тратим одноразовый шаг на мусор), но
            // ответ одинаковый: по нему нельзя понять, что именно не подошло.
            var totpOk = passwordOk && (!admin.RequireTotp || totp.Verify(request.Totp));
            if (!passwordOk || !totpOk)
            {
                var lockedNow = throttle.RegisterFailure();
                if (passwordOk)
                    logger.LogWarning("Админ-панель: верный пароль, но неверный код TOTP (IP {Ip})", ip);
                else
                    logger.LogWarning("Админ-панель: неверный логин или пароль (IP {Ip})", ip);
                if (lockedNow)
                    logger.LogError(
                        "Админ-панель: {MaxFailedLogins} неудачных входов подряд — вход заблокирован на {LockoutDuration}",
                        admin.MaxFailedLogins, admin.LockoutDuration);
                return Results.Json(new { code = "invalid_credentials" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            throttle.RegisterSuccess();
            var session = await sessions.CreateAsync(admin.SessionLifetime, ip, http.Request.Headers.UserAgent.ToString(), ct);
            var expiresAt = new DateTimeOffset(session.ExpiresAt, TimeSpan.Zero);
            var token = AdminSessionCookie.Issue(dataProtection, session.Id, expiresAt);
            http.Response.Cookies.Append(AdminCookieNames.Session, token, new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Expires = expiresAt,
                Path = "/",
            });
            logger.LogInformation("Админ-панель: успешный вход, сессия {SessionId} (IP {Ip})", session.Id, ip);
            return Results.Ok();
        }).AllowAnonymous();

        // AllowAnonymous и здесь: "выйди" — операция вида "приведи к состоянию X" (см.
        // patterns/backend.md) — вызов с уже недействительной/просроченной cookie не должен
        // требовать действительную сессию, просто очищает то, что есть (или ничего).
        // Действительная cookie при этом отзывается на сервере (строка AdminSessions, аудит
        // security-audit-2026-10, M3): её копия больше не пройдёт AdminAuthenticationHandler, и это
        // переживает рестарт API.
        group.MapDelete("", async (
            HttpContext http, IDataProtectionProvider dataProtection, AdminSessionStore sessions,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (http.Request.Cookies.TryGetValue(AdminCookieNames.Session, out var token)
                && !string.IsNullOrEmpty(token)
                && AdminSessionCookie.Validate(dataProtection, token) is { } session)
            {
                await sessions.RevokeAsync(session.Id, ct);
                loggerFactory.CreateLogger("FamilyHub.Admin.Session").LogInformation(
                    "Админ-панель: выход, сессия отозвана (IP {Ip})", http.Connection.RemoteIpAddress?.ToString());
            }

            http.Response.Cookies.Delete(AdminCookieNames.Session, new CookieOptions { Path = "/" });
            return Results.Ok();
        }).AllowAnonymous();

        // «Выйти на всех устройствах»: отзывает все живые сессии панели, включая текущую.
        group.MapPost("/revoke-all", async (
            HttpContext http, AdminSessionStore sessions, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var revoked = await sessions.RevokeAllAsync(ct);
            loggerFactory.CreateLogger("FamilyHub.Admin.Session").LogWarning(
                "Админ-панель: «выйти везде» — отозвано сессий: {Count} (IP {Ip})", revoked, http.Connection.RemoteIpAddress?.ToString());
            http.Response.Cookies.Delete(AdminCookieNames.Session, new CookieOptions { Path = "/" });
            return Results.Ok(new { revoked });
        }).RequireAuthorization("PlatformAdmin");

        // Проверка гардом Angular-роута (см. AdminHubComponent): 200, если cookie ещё
        // действительна, иначе стандартный 401 от политики PlatformAdmin.
        group.MapGet("", () => Results.Ok()).RequireAuthorization("PlatformAdmin");
    }
}
