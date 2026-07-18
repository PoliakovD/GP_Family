using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Api.Features.Families;

public record CreateFamilyRequest(string Name);

public static class FamilyEndpoints
{
    public static void MapFamilyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/families").RequireAuthorization();

        group.MapPost("/",
            async (CreateFamilyRequest request, FamilyService service, ICurrentUser currentUser,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(request.Name))
                    return Results.BadRequest("Имя семьи не может быть пустым.");

                var familyId = await service.CreateFamilyAsync(currentUser.UserId, request.Name.Trim(), ct);
                return Results.Created($"/api/families/{familyId}", new { id = familyId });
            });


       

        group.MapGet("/", async (FamilyService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.GetMyFamiliesAsync(currentUser.UserId, ct)));
    }
}