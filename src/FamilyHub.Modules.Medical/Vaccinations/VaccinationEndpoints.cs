using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Modules.Medical.Vaccinations;

public static class VaccinationEndpoints
{
    public static void MapVaccinationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/vaccinations").RequireAuthorization();

        group.MapGet("/overview", async (VaccinationService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.GetOverviewAsync(currentUser.UserId, ct)));

        group.MapGet("/attention-count", async (VaccinationService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(new AttentionCountResponse(await service.AttentionCountAsync(currentUser.UserId, ct))));

        group.MapGet("/catalog", () => Results.Ok(VaccinationService.GetCatalog()));

        group.MapGet("/people/{kind}/{id:guid}", async (
            string kind, Guid id, VaccinationService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item) = await service.GetPersonScheduleAsync(currentUser.UserId, kind, id, ct);
            return result == VaccinationResult.NotFound ? Results.NotFound() : Results.Ok(item);
        });

        group.MapGet("/people/{kind}/{id:guid}/series/{seriesCode}", async (
            string kind, Guid id, string seriesCode, VaccinationService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item) = await service.GetSeriesDetailAsync(currentUser.UserId, kind, id, seriesCode, ct);
            return result == VaccinationResult.NotFound ? Results.NotFound() : Results.Ok(item);
        });

        group.MapGet("/{id:guid}", async (
            Guid id, VaccinationService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item) = await service.GetCustomDetailAsync(currentUser.UserId, id, ct);
            return result == VaccinationResult.NotFound ? Results.NotFound() : Results.Ok(item);
        });

        group.MapPost("", async (
            CreateVaccinationRequest request, VaccinationService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item, error) = await service.CreateAsync(currentUser.UserId, request, ct);
            return result switch
            {
                VaccinationResult.NotFound => Results.NotFound(),
                VaccinationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                VaccinationResult.Invalid => Results.BadRequest(new { message = error }),
                _ => Results.Ok(item),
            };
        }).RequireRateLimiting("medical-write");

        group.MapPost("/bulk", async (
            BulkMarkRequest request, VaccinationService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item, error) = await service.BulkMarkAsync(currentUser.UserId, request, ct);
            return result switch
            {
                VaccinationResult.NotFound => Results.NotFound(),
                VaccinationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                VaccinationResult.Invalid => Results.BadRequest(new { message = error }),
                _ => Results.Ok(item),
            };
        }).RequireRateLimiting("medical-write");

        group.MapPut("/{id:guid}", async (
            Guid id, UpdateVaccinationRequest request, VaccinationService service, ICurrentUser currentUser, CancellationToken ct) =>
            Map(await service.UpdateAsync(currentUser.UserId, id, request, ct))).RequireRateLimiting("medical-write");

        group.MapDelete("/{id:guid}", async (
            Guid id, VaccinationService service, ICurrentUser currentUser, CancellationToken ct) =>
            Map(await service.DeleteAsync(currentUser.UserId, id, ct))).RequireRateLimiting("medical-write");
    }

    private static IResult Map(VaccinationResult result) => result switch
    {
        VaccinationResult.Success => Results.NoContent(),
        VaccinationResult.NotFound => Results.NotFound(),
        VaccinationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        _ => Results.BadRequest(new { message = "Некорректные данные." }),
    };
}
