using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Api.Features.Jobs;

/// <summary>Глобальный индикатор фоновых процессов (§4 плана «живой конвейер») — бейдж + выпадающий
/// список, видимые из любого экрана приложения, не только со страницы конкретной записи.</summary>
public static class UserJobsEndpoints
{
    public static void MapUserJobsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/jobs").RequireAuthorization();

        group.MapGet("/active-summary", async (UserJobsService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.BuildAsync(currentUser.UserId, ct)));
    }
}
