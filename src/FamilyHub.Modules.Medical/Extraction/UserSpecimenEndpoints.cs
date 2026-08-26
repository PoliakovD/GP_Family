using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Modules.Medical.Extraction;

public record CreateSpecimenRequest(string Name);

public static class UserSpecimenEndpoints
{
    public static void MapUserSpecimenEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/specimens").RequireAuthorization();

        group.MapGet("/", async (UserSpecimenService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.GetOwnAsync(currentUser.UserId, ct)));

        group.MapPost("/", async (
            CreateSpecimenRequest body, UserSpecimenService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item, reason) = await service.CreateAsync(currentUser.UserId, body.Name, ct);
            return result switch
            {
                CreateSpecimenResult.Success => Results.Created($"/api/specimens/{item!.Id}", item),
                CreateSpecimenResult.AlreadyExists => Results.Ok(item),
                CreateSpecimenResult.InvalidInput => Results.BadRequest(new { code = "invalid_input", reason }),
                CreateSpecimenResult.Rejected => Results.UnprocessableEntity(new { code = "rejected", reason }),
                CreateSpecimenResult.Unavailable => Results.Json(
                    new { code = "unavailable", reason }, statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.BadRequest(),
            };
        });
    }
}
