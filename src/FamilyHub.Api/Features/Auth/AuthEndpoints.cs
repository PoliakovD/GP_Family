using System.Security.Claims;
using FamilyHub.Api.Features.Bot;
using FamilyHub.Infrastructure.Auth.Jwt;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.CurrentUser;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Features.Auth;

public record StartCodeRequest(string Email);
public record ConfirmRegistrationRequest(string Email, string Code, string Password, string Username, string? DisplayName);
public record LoginRequest(string Email, string Password);
public record ConfirmLinkEmailRequest(string Email, string Code, string Password);
public record ConfirmResetPasswordRequest(string Email, string Code, string NewPassword);

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
            ConfirmRegistrationRequest request, PwaAuthService service, ITokenService tokenService,
            HttpContext http, CancellationToken ct) =>
        {
            var (result, userId) = await service.ConfirmRegistrationAsync(
                request.Email, request.Code, request.Password, request.Username, request.DisplayName, ct);
            if (result != ConfirmRegistrationResult.Success)
            {
                return result switch
                {
                    ConfirmRegistrationResult.InvalidCode => Results.BadRequest(new { code = "invalid_code" }),
                    ConfirmRegistrationResult.EmailTaken => Results.BadRequest(new { code = "email_taken" }),
                    ConfirmRegistrationResult.WeakPassword => Results.BadRequest(new { code = "weak_password" }),
                    ConfirmRegistrationResult.InvalidUsername => Results.BadRequest(new { code = "invalid_username" }),
                    _ => Results.BadRequest(new { code = "username_taken" }),
                };
            }
            return await IssueSessionAsync(userId, PwaAuthService.NormalizeEmail(request.Email), tokenService, http, ct);
        }).AllowAnonymous();

        // Проверка занятости username на форме регистрации (blur-хук на фронте). Формат
        // некорректен → available:false (тот же ответ, что и "занят" — не различаем на фронте
        // отдельным кодом здесь, invalid показывается фронтом по паттерну поля до запроса).
        group.MapGet("/username-available", async (string username, PwaAuthService service, CancellationToken ct) =>
        {
            var available = await service.IsUsernameAvailableAsync(username, ct);
            return Results.Ok(new { available });
        }).AllowAnonymous();

        group.MapPost("/login", async (
            LoginRequest request, PwaAuthService service, ITokenService tokenService,
            HttpContext http, CancellationToken ct) =>
        {
            var (result, user, lockedUntil) = await service.LoginAsync(request.Email, request.Password, ct);
            if (result != LoginResult.Success)
            {
                return result switch
                {
                    LoginResult.InvalidCredentials => Results.Json(new { code = "invalid_credentials" }, statusCode: StatusCodes.Status401Unauthorized),
                    _ => Results.Json(new { code = "locked_out", lockedUntil }, statusCode: StatusCodes.Status423Locked),
                };
            }
            return await IssueSessionAsync(user!.Id, user.Email!, tokenService, http, ct);
        }).AllowAnonymous();

        // Забыли пароль: тот же анти-enumeration ответ, что у register/start (всегда 200).
        group.MapPost("/reset-password/start", async (StartCodeRequest request, PwaAuthService service, CancellationToken ct) =>
        {
            await service.StartResetPasswordAsync(request.Email, ct);
            return Results.Ok();
        }).AllowAnonymous().RequireRateLimiting("auth-code");

        group.MapPost("/reset-password/confirm", async (
            ConfirmResetPasswordRequest request, PwaAuthService service, ITokenService tokenService,
            HttpContext http, CancellationToken ct) =>
        {
            var (result, userId) = await service.ConfirmResetPasswordAsync(request.Email, request.Code, request.NewPassword, ct);
            if (result != ResetPasswordResult.Success)
            {
                return result switch
                {
                    ResetPasswordResult.InvalidCode => Results.BadRequest(new { code = "invalid_code" }),
                    _ => Results.BadRequest(new { code = "weak_password" }),
                };
            }
            return await IssueSessionAsync(userId, PwaAuthService.NormalizeEmail(request.Email), tokenService, http, ct);
        }).AllowAnonymous();

        // Отзывает refresh-токен ТЕКУЩЕГО устройства (не все сессии — logout-all для этого).
        group.MapPost("/logout", async (HttpContext http, ITokenService tokenService, CancellationToken ct) =>
        {
            if (http.Request.Cookies.TryGetValue(PwaCookieNames.RefreshToken, out var refreshToken))
                await tokenService.RevokeAsync(refreshToken, ct);
            PwaSessionCookieWriter.ClearSessionCookies(http);
            return Results.Ok();
        });

        // Ротация: старый refresh — в утиль (revoke+ReplacedByTokenId), выдаются новые access+refresh.
        // AllowAnonymous — весь смысл эндпоинта в том, что access-токен уже истёк/отсутствует.
        group.MapPost("/refresh", async (HttpContext http, ITokenService tokenService, CancellationToken ct) =>
        {
            if (!http.Request.Cookies.TryGetValue(PwaCookieNames.RefreshToken, out var refreshToken))
                return Results.Unauthorized();

            var session = await tokenService.RefreshAsync(
                refreshToken, http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString(), ct);
            if (session is null)
            {
                PwaSessionCookieWriter.ClearSessionCookies(http);
                return Results.Unauthorized();
            }

            PwaSessionCookieWriter.SetSessionCookies(http, session);
            return Results.Ok();
        }).AllowAnonymous();

        // Logout со ВСЕХ устройств (после смены пароля/подозрения на компрометацию/merge источника).
        group.MapPost("/logout-all", async (ICurrentUser currentUser, ITokenService tokenService, HttpContext http, CancellationToken ct) =>
        {
            await tokenService.RevokeAllForUserAsync(currentUser.UserId, ct);
            PwaSessionCookieWriter.ClearSessionCookies(http);
            return Results.Ok();
        });

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
                username = user.Username,
                tgUsername = user.TgUsername,
                hasTelegram = user.TelegramId is not null,
                hasPassword = user.PasswordHash is not null,
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
                currentUser.UserId, request.Email, request.Code, request.Password, ct);
            return result switch
            {
                LinkEmailResult.InvalidCode => Results.BadRequest(new { code = "invalid_code" }),
                LinkEmailResult.EmailTaken => Results.BadRequest(new { code = "email_taken" }),
                LinkEmailResult.WeakPassword => Results.BadRequest(new { code = "weak_password" }),
                _ => Results.Ok(),
            };
        });

        // Привязка Telegram к текущему (email/PWA) аккаунту "с подтверждением с другой
        // стороны": выдаём одноразовый код + deep-link, пользователь подтверждает в боте
        // (TelegramUpdateHandler + AccountMergeService при коллизии с существующим TG-аккаунтом).
        // Статус — через /me (hasTelegram), отдельного эндпоинта поллинга не нужно.
        group.MapPost("/link-telegram/start", async (
            TelegramLinkService service, ICurrentUser currentUser, IOptions<TelegramOptions> telegramOptions, CancellationToken ct) =>
        {
            var botUsername = telegramOptions.Value.BotUsername;
            if (string.IsNullOrWhiteSpace(botUsername))
                return Results.Json(new { code = "bot_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);

            var (result, code, expiresAt) = await service.StartAsync(currentUser.UserId, ct);
            if (result == StartLinkTelegramResult.AlreadyLinked)
                return Results.Conflict(new { code = "already_linked" });

            var deepLink = $"https://t.me/{botUsername}?start={TelegramUpdateHandler.LinkPrefix}{code}";
            return Results.Ok(new { code, deepLink, expiresAt });
        }).RequireRateLimiting("auth-code");
    }

    /// <summary>Выпуск JWT-сессии: access+refresh в httpOnly cookie. UserId/Email/SessionId в
    /// access-токене — минимум ПДн (только email, без username/displayName), как раньше в
    /// cookie-тикете.</summary>
    private static async Task<IResult> IssueSessionAsync(
        Guid userId, string email, ITokenService tokenService, HttpContext http, CancellationToken ct)
    {
        var session = await tokenService.IssueAsync(
            userId, email, http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString(), ct);
        PwaSessionCookieWriter.SetSessionCookies(http, session);
        return Results.Ok();
    }
}
