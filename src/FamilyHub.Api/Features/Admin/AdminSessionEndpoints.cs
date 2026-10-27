using FamilyHub.Api.Configuration;
using FamilyHub.Api.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Features.Admin;

public record AdminLoginRequest(string User, string Password);

/// <summary>Вход/выход/проверка сессии админ-панели (ADR-0009). См. AdminAuthenticationHandler
/// для проверки cookie на каждый последующий запрос к /api/admin/*.</summary>
public static class AdminSessionEndpoints
{
    public static void MapAdminSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/session").RequireRateLimiting("auth");

        group.MapPost("", (
            AdminLoginRequest request, HttpContext http, IOptions<AdminOptions> options,
            IDataProtectionProvider dataProtection, AdminLoginThrottle throttle, ILoggerFactory loggerFactory) =>
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
            if (!CredentialComparer.Matches(request.User, request.Password, admin.User, admin.Password))
            {
                var lockedNow = throttle.RegisterFailure();
                logger.LogWarning("Админ-панель: неверный логин или пароль (IP {Ip})", ip);
                if (lockedNow)
                    logger.LogError(
                        "Админ-панель: {MaxFailedLogins} неудачных входов подряд — вход заблокирован на {LockoutDuration}",
                        admin.MaxFailedLogins, admin.LockoutDuration);
                return Results.Json(new { code = "invalid_credentials" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            throttle.RegisterSuccess();
            var token = AdminSessionCookie.Issue(dataProtection, admin.SessionLifetime);
            http.Response.Cookies.Append(AdminCookieNames.Session, token, new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Expires = DateTimeOffset.UtcNow.Add(admin.SessionLifetime),
                Path = "/",
            });
            logger.LogInformation("Админ-панель: успешный вход (IP {Ip})", ip);
            return Results.Ok();
        }).AllowAnonymous();

        // AllowAnonymous и здесь: "выйди" — операция вида "приведи к состоянию X" (см.
        // patterns/backend.md) — вызов с уже недействительной/просроченной cookie не должен
        // требовать действительную сессию, просто очищает то, что есть (или ничего).
        // Действительная cookie при этом отзывается на сервере (аудит security-audit-2026-10, M3):
        // её копия больше не пройдёт AdminAuthenticationHandler.
        group.MapDelete("", (
            HttpContext http, IDataProtectionProvider dataProtection, AdminSessionRevocations revocations,
            ILoggerFactory loggerFactory) =>
        {
            if (http.Request.Cookies.TryGetValue(AdminCookieNames.Session, out var token)
                && !string.IsNullOrEmpty(token)
                && AdminSessionCookie.Validate(dataProtection, token) is { } session)
            {
                revocations.Revoke(session.Id, session.ExpiresAt);
                loggerFactory.CreateLogger("FamilyHub.Admin.Session").LogInformation(
                    "Админ-панель: выход, сессия отозвана (IP {Ip})", http.Connection.RemoteIpAddress?.ToString());
            }

            http.Response.Cookies.Delete(AdminCookieNames.Session, new CookieOptions { Path = "/" });
            return Results.Ok();
        }).AllowAnonymous();

        // Проверка гардом Angular-роута (см. AdminHubComponent): 200, если cookie ещё
        // действительна, иначе стандартный 401 от политики PlatformAdmin.
        group.MapGet("", () => Results.Ok()).RequireAuthorization("PlatformAdmin");
    }
}
