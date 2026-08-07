using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Api.Features.Dependents;

public static class FamilyDependentEndpoints
{
    public static void MapFamilyDependentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization();

        group.MapGet("/families/{familyId:guid}/dependents", async (
            Guid familyId, FamilyDependentService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, items) = await service.GetForFamilyAsync(familyId, currentUser.UserId, ct);
            return result == FamilyDependentAccessResult.Forbidden ? Results.Forbid() : Results.Ok(items);
        });

        group.MapPost("/families/{familyId:guid}/dependents", async (
            Guid familyId, CreateFamilyDependentRequest request, FamilyDependentService service,
            ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item) = await service.CreateAsync(familyId, currentUser.UserId, request, ct);
            return result == FamilyDependentAccessResult.Forbidden
                ? Results.Forbid()
                : Results.Created($"/api/dependents/{item!.Id}", item);
        });

        group.MapPut("/dependents/{dependentId:guid}", async (
            Guid dependentId, UpdateFamilyDependentRequest request, FamilyDependentService service,
            ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.UpdateAsync(dependentId, currentUser.UserId, request, ct);
            return result switch
            {
                FamilyDependentAccessResult.NotFound => Results.NotFound(),
                FamilyDependentAccessResult.Forbidden => Results.Forbid(),
                _ => Results.NoContent(),
            };
        });

        // Только Admin семьи — каскадное удаление связанных MedicalRecord + физическая чистка
        // MinIO (см. FamilyDependentService.DeleteAsync).
        group.MapDelete("/dependents/{dependentId:guid}", async (
            Guid dependentId, FamilyDependentService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.DeleteAsync(dependentId, currentUser.UserId, ct);
            return result switch
            {
                FamilyDependentAccessResult.NotFound => Results.NotFound(),
                FamilyDependentAccessResult.Forbidden => Results.Forbid(),
                _ => Results.NoContent(),
            };
        });
    }
}
