using System.Security.Claims;
using FamilyHub.Infrastructure.Auth;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.CurrentUser;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Api.Features.Auth;

public record StartCodeRequest(string Email);
public record ConfirmRegistrationRequest(string Email, string Code, string Pin, string? DisplayName);
public record LoginRequest(string Email, string Pin);
public record ConfirmLinkEmailRequest(string Email, string Code, string Pin);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").RequireRateLimiting("auth");

        // Анти-enumeration: и Sent, и Throttled → 200, существование адреса не раскрывается.
        group.MapPost("/register/start", async (StartCodeRequest request, PwaAuthService service, CancellationToken ct) =>
        {
            await service.StartRegistrationAsync(request.Email, ct);
            return Results.Ok();
        }).AllowAnonymous().RequireRateLimiting("auth-code");

        group.MapPost("/register/confirm", async (
            ConfirmRegistrationRequest request, PwaAuthService service, CancellationToken ct) =>
        {
            var (result, userId) = await service.ConfirmRegistrationAsync(
                request.Email, request.Code, request.Pin, request.DisplayName, ct);
            return result switch
            {
                ConfirmRegistrationResult.InvalidCode => Results.BadRequest(new { code = "invalid_code" }),
                ConfirmRegistrationResult.EmailTaken => Results.BadRequest(new { code = "email_taken" }),
                ConfirmRegistrationResult.WeakPin => Results.BadRequest(new { code = "weak_pin" }),
                _ => SignInAsPwa(userId),
            };
        }).AllowAnonymous();

        group.MapPost("/login", async (LoginRequest request, PwaAuthService service, CancellationToken ct) =>
        {
            var (result, user, lockedUntil) = await service.LoginAsync(request.Email, request.Pin, ct);
            return result switch
            {
                LoginResult.InvalidCredentials => Results.Json(new { code = "invalid_credentials" }, statusCode: StatusCodes.Status401Unauthorized),
                LoginResult.LockedOut => Results.Json(new { code = "locked_out", lockedUntil }, statusCode: StatusCodes.Status423Locked),
                _ => SignInAsPwa(user!.Id),
            };
        }).AllowAnonymous();

        group.MapPost("/logout", () => Results.SignOut(authenticationSchemes: [AuthSchemes.PwaCookie]));

        group.MapGet("/me", async (ICurrentUser currentUser, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == currentUser.UserId, ct);
            var provider = principal.FindFirst(FamilyHubClaimTypes.AuthProvider)?.Value
                ?? (principal.FindFirst(FamilyHubClaimTypes.TelegramId) is null ? "email" : "telegram");
            return Results.Ok(new
            {
                userId = user.Id,
                displayName = user.DisplayName,
                provider,
                email = user.Email,
                hasTelegram = user.TelegramId is not null,
                hasPin = user.PinHash is not null,
            });
        });

        group.MapPost("/link-email/start", async (
            StartCodeRequest request, PwaAuthService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            await service.StartLinkEmailAsync(currentUser.UserId, request.Email, ct);
            return Results.Ok();
        }).RequireRateLimiting("auth-code");

        group.MapPost("/link-email/confirm", async (
            ConfirmLinkEmailRequest request, PwaAuthService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.ConfirmLinkEmailAsync(
                currentUser.UserId, request.Email, request.Code, request.Pin, ct);
            return result switch
            {
                LinkEmailResult.InvalidCode => Results.BadRequest(new { code = "invalid_code" }),
                LinkEmailResult.EmailTaken => Results.BadRequest(new { code = "email_taken" }),
                LinkEmailResult.WeakPin => Results.BadRequest(new { code = "weak_pin" }),
                _ => Results.Ok(),
            };
        });
    }

    /// <summary>Выпуск cookie-сессии PwaCookie: только UserId + маркер провайдера, без ПДн в клеймах.</summary>
    private static IResult SignInAsPwa(Guid userId)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(FamilyHubClaimTypes.UserId, userId.ToString()),
                new Claim(FamilyHubClaimTypes.AuthProvider, "email"),
            ],
            AuthSchemes.PwaCookie);

        return Results.SignIn(
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true },
            AuthSchemes.PwaCookie);
    }
}
