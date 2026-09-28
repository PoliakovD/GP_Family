using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Modules.Medical.HealthSummary;

public static class HealthSummaryEndpoints
{
    public static void MapHealthSummaryEndpoints(this IEndpointRouteBuilder app)
    {
        // Хаб «Здоровье» (редизайн навигации) — одна сводка вместо восьми запросов на превью плиток.
        app.MapGet("/api/health/summary", async (
            HealthSummaryService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.BuildAsync(currentUser.UserId, ct)))
            .RequireAuthorization();
    }
}
