using FamilyHub.Infrastructure.CurrentUser;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Api.Features.Auth;

public record TelegramInitRequest(string InitData);
public record TelegramSendCodeRequest(string Email, string InitData);
public record TelegramBindRequest(string Email, string Code, string InitData);

public static class TelegramBindingEndpoints
{
    public static void MapTelegramBindingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth/telegram").RequireRateLimiting("auth");

        // Все три ниже — AllowAnonymous и явно валидируют initData внутри сервиса, а не через
        // ASP.NET auth pipeline: TelegramMiniAppAuthenticationHandler теперь lookup-only и упал
        // бы 401 на ещё не привязанный TelegramId раньше, чем запрос дошёл бы сюда.
        group.MapPost("/init", async (TelegramInitRequest request, TelegramBindingService service, CancellationToken ct) =>
        {
            var result = await service.InitAsync(request.InitData, ct);
            return result switch
            {
                TelegramInitResult.Bound => Results.Ok(new { bound = true }),
                TelegramInitResult.BindingRequired => Results.Ok(new { bound = false }),
                _ => Results.BadRequest(new { code = "invalid_init_data" }),
            };
        }).AllowAnonymous();

        // Анти-enumeration: Sent и Throttled → одинаковый 200 (см. PwaAuthService).
        group.MapPost("/send-code", async (TelegramSendCodeRequest request, TelegramBindingService service, CancellationToken ct) =>
        {
            var result = await service.SendCodeAsync(request.Email, request.InitData, ct);
            return result == TelegramSendCodeResult.InvalidInitData
                ? Results.BadRequest(new { code = "invalid_init_data" })
                : Results.Ok();
        }).AllowAnonymous().RequireRateLimiting("auth-code");

        group.MapPost("/bind", async (TelegramBindRequest request, TelegramBindingService service, CancellationToken ct) =>
        {
            var result = await service.ConfirmBindAsync(request.Email, request.Code, request.InitData, ct);
            return result switch
            {
                TelegramBindResult.Success => Results.Ok(),
                TelegramBindResult.InvalidCode => Results.BadRequest(new { code = "invalid_code" }),
                TelegramBindResult.InvalidInitData => Results.BadRequest(new { code = "invalid_init_data" }),
                TelegramBindResult.EmailLinkedToDifferentTelegram =>
                    Results.Conflict(new { code = "email_linked_to_different_telegram" }),
                _ => Results.Conflict(new { code = "telegram_already_bound" }),
            };
        }).AllowAnonymous();

        // Отвязка (компрометация Telegram): из PWA-сессии. Ничего, кроме TelegramId, отзывать
        // не нужно — у Telegram нет сессии/токена, аутентификация per-request по initData +
        // lookup по TelegramId; после этой строки такой lookup для украденного TelegramId
        // ничего не найдёт, и следующий же запрос из Telegram получит 401.
        group.MapPost("/revoke", async (ICurrentUser currentUser, AppDbContext db, CancellationToken ct) =>
        {
            await db.Users.Where(u => u.Id == currentUser.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.TelegramId, (long?)null), ct);
            return Results.Ok();
        });
    }
}
