using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Api.Features.Home;

public static class HomeEndpoints
{
    public static void MapHomeEndpoints(this IEndpointRouteBuilder app)
    {
        // Агрегат содержит медданные (статус анализов) — под ConsentRequiredFilter, как и
        // остальные медицинские эндпоинты (см. Program.cs, обёртка группы этого маппинга).
        app.MapGet("/api/home/summary", async (
                HomeSummaryService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.BuildAsync(currentUser.UserId, ct)))
            .RequireAuthorization();
    }
}
