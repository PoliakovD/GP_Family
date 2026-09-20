namespace FamilyHub.Api.Features.Admin;

/// <summary>
/// Прогрев кэша веб-поиска из админки (см. class doc SearchWarmupRun/SearchCacheWarmupJob) —
/// вкладка «Прогрев» внутри /admin/enrichment. Отдельный файл от AdminEnrichmentEndpoints, но та
/// же группа маршрутов — прогрев логически часть той же страницы про платный поиск.
/// </summary>
public static class AdminWarmupEndpoints
{
    public static void MapAdminWarmupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/enrichment").RequireAuthorization("PlatformAdmin");

        group.MapPost("/warmup", async (
            StartWarmupRequest request, AdminSearchWarmupService warmup, CancellationToken ct) =>
        {
            var (result, status) = await warmup.StartAsync(request, ct);
            return result switch
            {
                StartWarmupResult.SpecimenRequired => Results.Json(
                    new { code = "specimen_required", message = "Для темы «Показатели» нужно выбрать биоматериал." },
                    statusCode: StatusCodes.Status400BadRequest),
                StartWarmupResult.NothingToDo => Results.Json(
                    new { code = "nothing_to_do", message = "После разбора список пуст — нечего прогревать." },
                    statusCode: StatusCodes.Status400BadRequest),
                StartWarmupResult.AlreadyRunning => Results.Json(
                    new { code = "already_running", message = "Прогрев уже идёт.", status },
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.Accepted(value: status),
            };
        });

        group.MapPost("/warmup/cancel", async (AdminSearchWarmupService warmup, CancellationToken ct) =>
            await warmup.CancelAsync(ct) ? Results.NoContent() : Results.NotFound());

        group.MapGet("/warmup/status", async (AdminSearchWarmupService warmup, CancellationToken ct) =>
            Results.Ok(await warmup.GetStatusAsync(ct)));
    }
}
