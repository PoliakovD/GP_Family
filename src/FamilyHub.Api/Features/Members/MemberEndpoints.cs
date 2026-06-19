using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Api.Features.Members;

public static class MemberEndpoints
{
    public static void MapMemberEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/families").RequireAuthorization();

        group.MapPost("/{familyId:guid}/members/{targetUserId:guid}/remove", async (
            Guid familyId, Guid targetUserId, MembershipService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.RemoveMemberAsync(familyId, targetUserId, currentUser.UserId, ct);
            return result switch
            {
                RemoveMemberResult.Forbidden => Results.Forbid(),
                RemoveMemberResult.NotFound => Results.NotFound(),
                RemoveMemberResult.LastAdmin => Results.Conflict("Нельзя удалить последнего админа семьи."),
                _ => Results.NoContent(),
            };
        });

        group.MapPost("/{familyId:guid}/leave", async (
            Guid familyId, MembershipService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.LeaveFamilyAsync(familyId, currentUser.UserId, ct);
            return result switch
            {
                LeaveFamilyResult.NotFound => Results.NotFound(),
                LeaveFamilyResult.LastAdmin => Results.Conflict("Вы последний админ — назначьте другого, прежде чем выйти."),
                _ => Results.NoContent(),
            };
        });
    }
}
