using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Modules.Medical.MedicalRecords;

public record ShareFamilyRequest(Guid FamilyId);

public static class MedicalRecordEndpoints
{
    public static void MapMedicalRecordEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/medical-records").RequireAuthorization();

        group.MapGet("/", async (MedicalRecordService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.GetVisibleRecordsAsync(currentUser.UserId, ct)));

        group.MapPost("/", async (
            CreateMedicalRecordRequest request, MedicalRecordService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(currentUser.UserId, request, ct);
            return Results.Created($"/api/medical-records/{created.Id}", created);
        });

        group.MapPost("/share", async (
            ShareFamilyRequest request, MedicalRecordService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.ShareWithFamilyAsync(currentUser.UserId, request.FamilyId, ct);
            return result == MedicalRecordAccessResult.Forbidden ? Results.Forbid() : Results.NoContent();
        });

        group.MapPost("/unshare", async (
            ShareFamilyRequest request, MedicalRecordService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.UnshareFamilyAsync(currentUser.UserId, request.FamilyId, ct);
            return result == MedicalRecordAccessResult.NotFound ? Results.NotFound() : Results.NoContent();
        });

        group.MapPost("/{recordId:guid}/hide", async (
            Guid recordId, FamilyIdsRequest request, MedicalRecordService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.HideFromFamiliesAsync(currentUser.UserId, recordId, request.FamilyIds, ct);
            return MapResult(result);
        });

        group.MapPost("/{recordId:guid}/unhide", async (
            Guid recordId, FamilyIdsRequest request, MedicalRecordService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.UnhideFromFamiliesAsync(currentUser.UserId, recordId, request.FamilyIds, ct);
            return MapResult(result);
        });
    }

    private static IResult MapResult(MedicalRecordAccessResult result) => result switch
    {
        MedicalRecordAccessResult.NotFound => Results.NotFound(),
        MedicalRecordAccessResult.Forbidden => Results.Forbid(),
        _ => Results.NoContent(),
    };
}
