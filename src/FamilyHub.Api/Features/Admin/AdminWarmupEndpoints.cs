using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Enrichment;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FamilyHub.Api.Features.Admin;

public record WebSearchValveDto(bool IsPaused, DateTime? PausedAt, string? Note);

public record SetWebSearchValveRequest(bool IsPaused, string? Note);

/// <summary>
/// Прогрев кэша веб-поиска из админки (см. class doc SearchWarmupRun/SearchCacheWarmupJob) и
/// вентиль платного поиска (ADR-0005 §9, замена месячной квоты) — вкладка «Прогрев» и переключатель
/// вентиля внутри /admin/enrichment. Отдельный файл от AdminEnrichmentEndpoints, но та же группа
/// маршрутов — обе темы про один и тот же платный внешний вызов.
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

        // Вентиль платного поиска (ADR-0005 §9) — GET читает через сам сервис (единственная строка,
        // без кеша — см. class doc WebSearchValveService), PUT переключает и, на открытии, ставит
        // возобновление отложенных задач (DeferredEnrichmentReleaseJob), а также сбрасывает 60-
        // секундный кэш «Требует внимания» (AdminPipelineEndpoints.AttentionCacheKey), иначе баннер
        // «на паузе» протух бы до минуты после уже состоявшегося переключения.
        group.MapGet("/web-search", async (AppDbContext db, CancellationToken ct) =>
        {
            var row = await db.WebSearchConfigs.AsNoTracking().FirstOrDefaultAsync(ct);
            return Results.Ok(new WebSearchValveDto(row?.IsPaused ?? false, row?.PausedAt, row?.Note));
        });

        group.MapPut("/web-search", async (
            SetWebSearchValveRequest request, IWebSearchValveService valve, IMemoryCache cache,
            IBackgroundJobClient backgroundJobs, CancellationToken ct) =>
        {
            await valve.SetPausedAsync(request.IsPaused, request.Note, ct);
            cache.Remove(AdminPipelineEndpoints.AttentionCacheKey);

            if (!request.IsPaused)
                backgroundJobs.Enqueue<DeferredEnrichmentReleaseJob>(j => j.RunAsync(CancellationToken.None));

            return Results.NoContent();
        });
    }
}
