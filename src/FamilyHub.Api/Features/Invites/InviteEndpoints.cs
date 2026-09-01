using FamilyHub.Api.Features.Families;
using FamilyHub.Contracts.BotApi;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.CurrentUser;
using FamilyHub.Infrastructure.Email;
using FamilyHub.Infrastructure.Telegram;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Features.Invites;

public static class InviteEndpoints
{
    public static void MapInviteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization();

        group.MapPost("/families/{familyId:guid}/invites", async (
            Guid familyId, CreateInviteRequest request, InviteService service,
            ICurrentUser currentUser, IOptions<TelegramOptions> telegramOptions, IOptions<EmailOptions> emailOptions,
            CancellationToken ct) =>
        {
            var (result, invite) = await service.CreateInviteAsync(currentUser.UserId, familyId, request, ct);
            if (result == CreateInviteResult.Forbidden)
                return Results.Forbid();

            var botUsername = telegramOptions.Value.BotUsername;
            var telegramLink = string.IsNullOrWhiteSpace(botUsername)
                ? (string?)null
                : $"https://t.me/{botUsername}?start={BotDeepLinks.InvitePrefix}{invite!.Code}";

            // Основная ссылка ведёт на сайт (PWA), Telegram — отдельная кнопка на фронте.
            // PublicSiteUrl уже валидируется как абсолютный http(s)-URL в Program.cs (см. письма).
            var webLink = $"{emailOptions.Value.PublicSiteUrl.TrimEnd('/')}/join/{invite!.Code}";

            return Results.Created($"/api/invites/{invite!.Id}",
                new { invite.Id, invite.Code, invite.MaxUses, invite.ExpiresAt, WebLink = webLink, TelegramLink = telegramLink });
        });

        // Анонимный превью для лендинга /join/:code — гость видит, куда его зовут, до входа/регистрации.
        group.MapGet("/invites/{code}/preview", async (
            string code, InviteService service, CancellationToken ct) =>
        {
            var (result, preview) = await service.GetPreviewAsync(code, ct);
            return result switch
            {
                InvitePreviewResult.NotFound => Results.NotFound(),
                // reason (не status) — ApiService.toApiError на фронте читает body.reason в ApiError.message,
                // так JoinInviteComponent различает причины без отдельного парсинга тела ответа.
                InvitePreviewResult.Revoked => Results.Conflict(new { reason = "revoked" }),
                InvitePreviewResult.Expired => Results.Conflict(new { reason = "expired" }),
                InvitePreviewResult.Exhausted => Results.Conflict(new { reason = "exhausted" }),
                _ => Results.Ok(preview),
            };
        }).AllowAnonymous().RequireRateLimiting("invite-redeem");

        group.MapGet("/families/{familyId:guid}/current",
            async (Guid familyId, FamilyService service, ICurrentUser currentUser, CancellationToken ct) =>
            {
                var (result, members) = await service.GetFamilyMembersAsync(familyId, currentUser.UserId, ct);
                return result == GetFamilyMembersResult.Forbidden ? Results.Forbid() : Results.Ok(members);
            }
        );

        group.MapPost("/invites/{code}/redeem", async (
            string code, InviteService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, familyId) = await service.RedeemInviteAsync(code, currentUser.UserId, ct);
            return result switch
            {
                RedeemResult.NotFound => Results.NotFound(),
                RedeemResult.Revoked => Results.Conflict("Инвайт отозван."),
                RedeemResult.Expired => Results.Conflict("Инвайт просрочен."),
                RedeemResult.Exhausted => Results.Conflict("Инвайт исчерпан."),
                RedeemResult.NotForYou => Results.Forbid(),
                // message (не только код) — ApiError.message на фронте читает body.reason ?? body.message
                // для объектных тел (см. ApiService.toApiError), чтобы существующий UI (families-tab) не потерял
                // человекочитаемый текст ошибки при появлении нового поля familyId.
                RedeemResult.AlreadyMember => Results.Conflict(new { message = "Вы уже состоите в этой семье.", familyId }),
                RedeemResult.Joined => Results.Ok(new { status = "joined", familyId }),
                RedeemResult.PendingApproval => Results.Ok(new { status = "pending_approval", familyId }),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        }).RequireRateLimiting("invite-redeem");

        group.MapPost("/invites/{inviteId:guid}/revoke", async (
            Guid inviteId, InviteService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.RevokeInviteAsync(inviteId, currentUser.UserId, ct);
            return result switch
            {
                RevokeInviteResult.NotFound => Results.NotFound(),
                RevokeInviteResult.Forbidden => Results.Forbid(),
                _ => Results.NoContent(),
            };
        });

        group.MapGet("/families/{familyId:guid}/pending", async (
            Guid familyId, InviteService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, pending) = await service.GetPendingMembersAsync(familyId, currentUser.UserId, ct);
            return result switch
            {
                ApproveRejectResult.Forbidden => Results.Forbid(),
                _ => Results.Ok(pending),
            };
        });

        group.MapPost("/families/{familyId:guid}/members/{targetUserId:guid}/approve", async (
            Guid familyId, Guid targetUserId, InviteService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.ApproveMemberAsync(familyId, targetUserId, currentUser.UserId, ct);
            return MapApproveReject(result);
        });

        group.MapPost("/families/{familyId:guid}/members/{targetUserId:guid}/reject", async (
            Guid familyId, Guid targetUserId, InviteService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.RejectMemberAsync(familyId, targetUserId, currentUser.UserId, ct);
            return MapApproveReject(result);
        });
    }

    private static IResult MapApproveReject(ApproveRejectResult result) => result switch
    {
        ApproveRejectResult.Forbidden => Results.Forbid(),
        ApproveRejectResult.NotFound => Results.NotFound(),
        _ => Results.NoContent(),
    };
}
