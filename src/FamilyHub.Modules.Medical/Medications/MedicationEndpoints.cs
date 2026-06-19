using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Modules.Medical.Medications;

public static class MedicationEndpoints
{
    public static void MapMedicationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization();

        group.MapGet("/families/{familyId:guid}/medications", async (
            Guid familyId, MedicationService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, items) = await service.GetForFamilyAsync(familyId, currentUser.UserId, ct);
            return result == MedicationAccessResult.Forbidden ? Results.Forbid() : Results.Ok(items);
        });

        group.MapPost("/families/{familyId:guid}/medications", async (
            Guid familyId, CreateMedicationRequest request, MedicationService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item) = await service.CreateAsync(familyId, currentUser.UserId, request, ct);
            return result == MedicationAccessResult.Forbidden
                ? Results.Forbid()
                : Results.Created($"/api/medications/{item!.Id}", item);
        });

        group.MapPut("/medications/{medicationId:guid}", async (
            Guid medicationId, UpdateMedicationRequest request, MedicationService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.UpdateAsync(medicationId, currentUser.UserId, request, ct);
            return result switch
            {
                MedicationAccessResult.NotFound => Results.NotFound(),
                MedicationAccessResult.Forbidden => Results.Forbid(),
                _ => Results.NoContent(),
            };
        });

        group.MapDelete("/medications/{medicationId:guid}", async (
            Guid medicationId, MedicationService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.DeleteAsync(medicationId, currentUser.UserId, ct);
            return result switch
            {
                MedicationAccessResult.NotFound => Results.NotFound(),
                MedicationAccessResult.Forbidden => Results.Forbid(),
                _ => Results.NoContent(),
            };
        });
    }
}
