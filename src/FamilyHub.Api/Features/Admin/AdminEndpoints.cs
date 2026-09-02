using FamilyHub.Modules.Medical.Extraction;
using Hangfire;
using Microsoft.Extensions.Caching.Memory;

namespace FamilyHub.Api.Features.Admin;

public static class AdminEndpoints
{
    private const string StorageStatsCacheKey = "admin:stats:storage";
    private static readonly TimeSpan StorageStatsCacheTtl = TimeSpan.FromMinutes(15);

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin").RequireAuthorization("PlatformAdmin");

        group.MapGet("/stats/overview", async (AdminStatsService stats, CancellationToken ct) =>
            Results.Ok(await stats.GetOverviewAsync(ct)));

        // Полный листинг бакета — дорогая операция (см. IFileStorage.ListAsync), поэтому
        // кэшируется на StorageStatsCacheTtl; ?recalculate=true (кнопка "Пересчитать" в UI)
        // обходит кэш. Ответ несёт ComputedAt — панель показывает "по состоянию на …".
        group.MapGet("/stats/storage", async (
            bool? recalculate, AdminStatsService stats, IMemoryCache cache, CancellationToken ct) =>
        {
            if (recalculate != true && cache.TryGetValue(StorageStatsCacheKey, out AdminStorageStatsDto? cached) && cached is not null)
                return Results.Ok(cached);

            var fresh = await stats.GetStorageStatsAsync(ct);
            cache.Set(StorageStatsCacheKey, fresh, StorageStatsCacheTtl);
            return Results.Ok(fresh);
        });

        group.MapGet("/stats/system", async (AdminStatsService stats, CancellationToken ct) =>
            Results.Ok(await stats.GetSystemStatsAsync(ct)));

        group.MapGet("/stats/security", async (AdminStatsService stats, CancellationToken ct) =>
            Results.Ok(await stats.GetSecurityStatsAsync(ct)));

        group.MapGet("/keys", (AdminStatsService stats) => Results.Ok(stats.GetKeyRings()));

        group.MapPost("/keys/encryption/rotate", async (AdminKeysService keys, CancellationToken ct) =>
        {
            var result = await keys.StartOrResumeRotationAsync(ct);
            return result == StartRotationResult.NothingToRotate
                ? Results.Json(new { code = "nothing_to_rotate" }, statusCode: StatusCodes.Status409Conflict)
                : Results.Accepted();
        });

        group.MapPost("/keys/encryption/rotate/cancel", async (AdminKeysService keys, CancellationToken ct) =>
        {
            var cancelled = await keys.RequestCancelAsync(ct);
            return cancelled ? Results.Ok() : Results.NotFound();
        });

        group.MapGet("/keys/encryption/rotate/status", async (AdminKeysService keys, CancellationToken ct) =>
            Results.Ok(await keys.GetStatusAsync(ct)));

        // Пересборка enrich-пайплайна анализов — принудительное переобогащение справочника
        // показателей батчами (см. LabAnalyteKbReenrichJob doc); первый батч ставится
        // автоматически после миграции на v4, повторные запуски — отсюда, пока строк со старой
        // схемой не останется.
        group.MapPost("/kb/lab-analytes/reenrich", (IBackgroundJobClient backgroundJobs) =>
        {
            backgroundJobs.Enqueue<LabAnalyteKbReenrichJob>(j => j.RunAsync(CancellationToken.None));
            return Results.Accepted();
        });

        // Полная пересборка справочника показателей поверх исправленной чистки имён/резолвинга
        // источника (§4.2 плана) — в отличие от reenrich выше (реагирует на дрейф PayloadVersion
        // построчно), разовое ручное действие после деплоя: пересчитывает ключи существующих
        // показателей, чистит справочник и пересеивает обогащение. См. LabAnalyteKbRebuildJob.
        group.MapPost("/kb/lab-analytes/rebuild", async (AdminKbRebuildService rebuild, CancellationToken ct) =>
        {
            var result = await rebuild.StartOrResumeAsync(ct);
            return result == StartKbRebuildResult.AlreadyRunning
                ? Results.Json(new { code = "already_running" }, statusCode: StatusCodes.Status409Conflict)
                : Results.Accepted();
        });

        group.MapGet("/kb/lab-analytes/rebuild/status", async (AdminKbRebuildService rebuild, CancellationToken ct) =>
            Results.Ok(await rebuild.GetStatusAsync(ct)));
    }
}
