using FamilyHub.Infrastructure.CurrentUser;
using FamilyHub.Modules.Medical.Medications;

namespace FamilyHub.Modules.Medical.Kb;

public static class KbEndpoints
{
    public static void MapKbEndpoints(this IEndpointRouteBuilder app)
    {
        // Общий справочник — раздел «Справочник», доступен любому вошедшему принявшему согласие
        // (справочник обезличен и глобален по определению, задача 2.6). Явный Cache-Control уже
        // проставляется общим middleware для всех /api/* (см. Program.cs) — здесь ничего доп. не нужно.
        var catalogGroup = app.MapGroup("/api/kb/medications").RequireAuthorization();

        catalogGroup.MapGet("/", async (
            string? q, int skip, int take, KbCatalogService catalog, CancellationToken ct) =>
            Results.Ok(await catalog.SearchAsync(q, skip, take, ct)));

        catalogGroup.MapGet("/{id:guid}", async (Guid id, KbCatalogService catalog, CancellationToken ct) =>
        {
            var card = await catalog.GetByIdAsync(id, ct);
            return card is null ? Results.NotFound() : Results.Ok(card);
        });

        // Статус обогащения/ручной рефреш конкретного медикамента пользователя — доступ, как и у
        // MedicationService, проверяется по роли Member в семье медикамента (не по факту знания
        // о справочнике, который сам по себе глобален).
        var medicationGroup = app.MapGroup("/api/medications/{medicationId:guid}/kb").RequireAuthorization();

        medicationGroup.MapGet("/", async (
            Guid medicationId, MedicationKbStatusService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, response) = await service.GetStatusAsync(medicationId, currentUser.UserId, ct);
            return result switch
            {
                MedicationAccessResult.NotFound => Results.NotFound(),
                MedicationAccessResult.Forbidden => Results.Forbid(),
                _ => Results.Ok(response),
            };
        });

        medicationGroup.MapPost("/refresh", async (
            Guid medicationId, MedicationKbStatusService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.RequestRefreshAsync(medicationId, currentUser.UserId, ct);
            return result switch
            {
                MedicationAccessResult.NotFound => Results.NotFound(),
                MedicationAccessResult.Forbidden => Results.Forbid(),
                _ => Results.Accepted(),
            };
        });
    }
}
