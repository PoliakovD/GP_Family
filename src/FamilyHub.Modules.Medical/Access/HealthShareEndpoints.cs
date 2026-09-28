using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Modules.Medical.Access;

public static class HealthShareEndpoints
{
    public static void MapHealthShareEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/health-shares").RequireAuthorization();

        group.MapGet("/mine", async (
            HealthShareService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.GetMineAsync(currentUser.UserId, ct)));

        group.MapGet("/shared-with-me", async (
            HealthShareService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.GetSharedWithMeAsync(currentUser.UserId, ct)));

        group.MapPut("/mine/{viewerUserId:guid}", async (
            Guid viewerUserId, SetHealthShareRequest request,
            HealthShareService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.SetAsync(currentUser.UserId, viewerUserId, request.Categories, ct);
            return result == HealthShareResult.Invalid ? Results.BadRequest() : Results.NoContent();
        }).RequireRateLimiting("medical-write");
    }
}
