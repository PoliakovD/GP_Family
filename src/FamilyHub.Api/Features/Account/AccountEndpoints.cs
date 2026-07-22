using FamilyHub.Infrastructure.Auth;
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
            DeleteAccountRequest request, AccountService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            if (request.Confirm != "DELETE")
                return Results.BadRequest(new { code = "confirmation_required" });

            var outcome = await service.DeleteAccountAsync(currentUser.UserId, ct);
            return outcome.Deleted
                ? Results.SignOut(authenticationSchemes: [AuthSchemes.PwaCookie])
                : Results.Conflict(new { code = "last_admin", families = outcome.BlockingFamilies });
        });

        // Экспорт данных субъекта: стрим zip прямо в ответ.
        group.MapGet("/export", (AccountService service, AttachmentService attachments, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Stream(async stream => await service.WriteExportZipAsync(currentUser.UserId, stream, attachments, ct),
                "application/zip", "familyhub-export.zip"));
    }
}
