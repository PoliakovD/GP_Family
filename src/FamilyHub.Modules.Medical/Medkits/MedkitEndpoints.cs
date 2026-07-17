using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Modules.Medical.Medkits;

public static class MedkitEndpoints
{
    public static void MapMedkitEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization();

        group.MapGet("/families/{familyId:guid}/medkits", async (
            Guid familyId, MedkitService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, items) = await service.GetForFamilyAsync(familyId, currentUser.UserId, ct);
            return result == MedkitAccessResult.Forbidden ? Results.Forbid() : Results.Ok(items);
        });

        group.MapPost("/families/{familyId:guid}/medkits", async (
            Guid familyId, CreateMedkitRequest request, MedkitService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item) = await service.CreateAsync(familyId, currentUser.UserId, request, ct);
            return result == MedkitAccessResult.Forbidden
                ? Results.Forbid()
                : Results.Created($"/api/medkits/{item!.Id}", item);
        });

        group.MapPut("/medkits/{medkitId:guid}", async (
            Guid medkitId, UpdateMedkitRequest request, MedkitService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.UpdateAsync(medkitId, currentUser.UserId, request, ct);
            return result switch
            {
                MedkitAccessResult.NotFound => Results.NotFound(),
                MedkitAccessResult.Forbidden => Results.Forbid(),
                _ => Results.NoContent(),
            };
        });

        group.MapDelete("/medkits/{medkitId:guid}", async (
            Guid medkitId, MedkitService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.DeleteAsync(medkitId, currentUser.UserId, ct);
            return result switch
            {
                MedkitAccessResult.NotFound => Results.NotFound(),
                MedkitAccessResult.Forbidden => Results.Forbid(),
                _ => Results.NoContent(),
            };
        });
    }
}
