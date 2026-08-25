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
            AdminLoginRequest request, HttpContext http,
            IOptions<AdminOptions> options, IDataProtectionProvider dataProtection) =>
        {
            var admin = options.Value;
            if (!CredentialComparer.Matches(request.User, request.Password, admin.User, admin.Password))
                return Results.Json(new { code = "invalid_credentials" }, statusCode: StatusCodes.Status401Unauthorized);

            var token = AdminSessionCookie.Issue(dataProtection, admin.SessionLifetime);
            http.Response.Cookies.Append(AdminCookieNames.Session, token, new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Expires = DateTimeOffset.UtcNow.Add(admin.SessionLifetime),
                Path = "/",
            });
            return Results.Ok();
        }).AllowAnonymous();

        // AllowAnonymous и здесь: "выйди" — операция вида "приведи к состоянию X" (см.
        // patterns/backend.md) — вызов с уже недействительной/просроченной cookie не должен
        // требовать действительную сессию, просто очищает то, что есть (или ничего).
        group.MapDelete("", (HttpContext http) =>
        {
            http.Response.Cookies.Delete(AdminCookieNames.Session, new CookieOptions { Path = "/" });
            return Results.Ok();
        }).AllowAnonymous();

        // Проверка гардом Angular-роута (см. AdminHubComponent): 200, если cookie ещё
        // действительна, иначе стандартный 401 от политики PlatformAdmin.
        group.MapGet("", () => Results.Ok()).RequireAuthorization("PlatformAdmin");
    }
}
