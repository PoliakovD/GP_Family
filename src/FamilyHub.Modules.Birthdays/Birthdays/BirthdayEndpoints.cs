using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Modules.Birthdays.Birthdays;

public static class BirthdayEndpoints
{
    public static void MapBirthdayEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization();

        group.MapGet("/families/{familyId:guid}/birthdays", async (
            Guid familyId, BirthdayService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, items) = await service.GetForFamilyAsync(familyId, currentUser.UserId, ct);
            return result == BirthdayAccessResult.Forbidden ? Results.Forbid() : Results.Ok(items);
        });

        group.MapPost("/families/{familyId:guid}/birthdays", async (
            Guid familyId, CreateBirthdayRequest request, BirthdayService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item) = await service.CreateAsync(familyId, currentUser.UserId, request, ct);
            return result == BirthdayAccessResult.Forbidden
                ? Results.Forbid()
                : Results.Created($"/api/birthdays/{item!.Id}", item);
        });

        group.MapPut("/birthdays/{birthdayId:guid}", async (
            Guid birthdayId, UpdateBirthdayRequest request, BirthdayService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.UpdateAsync(birthdayId, currentUser.UserId, request, ct);
            return result switch
            {
                BirthdayAccessResult.NotFound => Results.NotFound(),
                BirthdayAccessResult.Forbidden => Results.Forbid(),
                _ => Results.NoContent(),
            };
        });

        group.MapDelete("/birthdays/{birthdayId:guid}", async (
            Guid birthdayId, BirthdayService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.DeleteAsync(birthdayId, currentUser.UserId, ct);
            return result switch
            {
                BirthdayAccessResult.NotFound => Results.NotFound(),
                BirthdayAccessResult.Forbidden => Results.Forbid(),
                _ => Results.NoContent(),
            };
        });
    }
}
