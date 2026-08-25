using FamilyHub.Infrastructure.Auth.Jwt;
using FamilyHub.Infrastructure.CurrentUser;
using FamilyHub.Modules.Medical.Attachments;

namespace FamilyHub.Api.Features.Account;

public record DeleteAccountRequest(string Confirm);

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/account");

        // Право на забвение: подтверждение строкой — защита от случайного вызова.
        group.MapPost("/delete", async (
            DeleteAccountRequest request, AccountService service, ICurrentUser currentUser,
            HttpContext http, CancellationToken ct) =>
        {
            if (request.Confirm != "DELETE")
                return Results.BadRequest(new { code = "confirmation_required" });

            var outcome = await service.DeleteAccountAsync(currentUser.UserId, ct);
            if (!outcome.Deleted)
                return Results.Conflict(new { code = "last_admin", families = outcome.BlockingFamilies });

            // Аккаунт (и его UserSessions — см. AccountService.DeleteAccountAsync) уже стёрт из
            // БД; здесь только гасим PWA-сессию текущего запроса (Telegram-режим — no-op, cookie
            // никогда не выставлялась). Results.SignOut тут не подходит: PwaCookie теперь JWT-схема,
            // не реализующая IAuthenticationSignOutHandler.
            PwaSessionCookieWriter.ClearSessionCookies(http);
            return Results.Ok();
        });

        // Экспорт данных субъекта: стрим zip прямо в ответ.
        group.MapGet("/export", (AccountService service, AttachmentService attachments, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Stream(async stream => await service.WriteExportZipAsync(currentUser.UserId, stream, attachments, ct),
                "application/zip", "familyhub-export.zip"));

        // Профиль (identity rework): единственный путь записи ФИО/ДР/пола после создания User —
        // используется и настройками (SettingsProfileComponent), и первичным экраном сбора
        // профиля после Telegram-привязки (ProfileSetupComponent).
        group.MapPut("/profile", async (
            UpdateProfileRequest request, ProfileService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.UpdateAsync(currentUser.UserId, request, ct);
            return result == UpdateProfileResult.Success
                ? Results.Ok()
                : Results.BadRequest(new { code = "invalid_profile" });
        });
    }
}
