using FamilyHub.Domain.Enums;
using FamilyHub.Domain.HealthNotes;
using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Modules.Medical.HealthNotes;

public static class HealthNoteEndpoints
{
    public static void MapHealthNoteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/health-notes").RequireAuthorization();

        group.MapGet("", async (
            DateTime? from, DateTime? to, HealthNoteKind? kind,
            HealthNoteService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(currentUser.UserId, from, to, kind, ct)));

        // Справочные данные для формы (и будущих клиентов): показатели с единицами и границами
        // ввода, допустимые ключи локализации и факторов самочувствия.
        group.MapGet("/catalog", () => Results.Ok(new HealthNoteCatalogResponse(
            HealthMetricCatalog.All, HealthNoteRules.BodyAreas, HealthNoteRules.WellbeingFactors)));

        group.MapGet("/recent", async (
            HealthNoteKind kind, HealthNoteService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.GetRecentTitlesAsync(currentUser.UserId, kind, ct: ct)));

        group.MapGet("/metrics/{code}/series", async (
            string code, DateTime? from, DateTime? to,
            HealthNoteService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var points = await service.GetMetricSeriesAsync(currentUser.UserId, code, from, to, ct);
            return points is null ? Results.NotFound() : Results.Ok(points);
        });

        group.MapPost("", async (
            HealthNoteRequest request, HealthNoteService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item, error) = await service.CreateAsync(currentUser.UserId, request, ct);
            return result == HealthNoteResult.Invalid
                ? Results.BadRequest(new { message = error })
                : Results.Created($"/api/health-notes/{item!.Id}", item);
        }).RequireRateLimiting("medical-write");

        group.MapPut("/{noteId:guid}", async (
            Guid noteId, HealthNoteRequest request, HealthNoteService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item, error) = await service.UpdateAsync(currentUser.UserId, noteId, request, ct);
            return result switch
            {
                HealthNoteResult.NotFound => Results.NotFound(),
                HealthNoteResult.Invalid => Results.BadRequest(new { message = error }),
                _ => Results.Ok(item),
            };
        }).RequireRateLimiting("medical-write");

        group.MapDelete("/{noteId:guid}", async (
            Guid noteId, HealthNoteService service, ICurrentUser currentUser, CancellationToken ct) =>
            await service.DeleteAsync(currentUser.UserId, noteId, ct) == HealthNoteResult.NotFound
                ? Results.NotFound()
                : Results.NoContent());
    }
}
