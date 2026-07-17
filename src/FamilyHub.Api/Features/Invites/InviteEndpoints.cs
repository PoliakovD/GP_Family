using FamilyHub.Api.Features.Bot;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.CurrentUser;
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
            ICurrentUser currentUser, IOptions<TelegramOptions> telegramOptions, CancellationToken ct) =>
        {
            var (result, invite) = await service.CreateInviteAsync(currentUser.UserId, familyId, request, ct);
            if (result == CreateInviteResult.Forbidden)
                return Results.Forbid();

            var botUsername = telegramOptions.Value.BotUsername;
            var telegramLink = string.IsNullOrWhiteSpace(botUsername)
                ? (string?)null
                : $"https://t.me/{botUsername}?start={TelegramUpdateHandler.InvitePrefix}{invite!.Code}";

            return Results.Created($"/api/invites/{invite!.Id}",
                new { invite.Id, invite.Code, invite.MaxUses, invite.ExpiresAt, TelegramLink = telegramLink });
        });

        group.MapPost("/invites/{code}/redeem", async (
            string code, InviteService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.RedeemInviteAsync(code, currentUser.UserId, ct);
            return result switch
            {
                RedeemResult.NotFound => Results.NotFound(),
                RedeemResult.Revoked => Results.Conflict("Инвайт отозван."),
                RedeemResult.Expired => Results.Conflict("Инвайт просрочен."),
                RedeemResult.Exhausted => Results.Conflict("Инвайт исчерпан."),
                RedeemResult.NotForYou => Results.Forbid(),
                RedeemResult.AlreadyMember => Results.Conflict("Вы уже состоите в этой семье."),
                RedeemResult.Joined => Results.Ok(new { status = "joined" }),
                RedeemResult.PendingApproval => Results.Ok(new { status = "pending_approval" }),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        });

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
