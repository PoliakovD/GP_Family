using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.Kb;
using Microsoft.Extensions.Caching.Memory;

namespace FamilyHub.Api.Features.Admin;

/// <summary>
/// История правок справочников и кэша поиска (ADR-0018): кто/когда/было/стало и откат одной версии.
/// Журнал пишут писатели kb (автообогащение, «system»), каталог админки и очередь «Одобрение» («admin»).
/// Только админская поверхность — пользовательский фронт и DTO журнал не видят.
/// </summary>
public static class AdminHistoryEndpoints
{
    private const int DefaultTake = 30;
    private const int MaxTake = 200;

    public static void MapAdminHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/history").RequireAuthorization("PlatformAdmin");

        // target — имя KbChangeTarget (LabAnalyteKb|MedicationKb|LabAnalyteSearchCache|MedicationSearchCache).
        group.MapGet("", async (
            string? target, Guid? targetId, int? skip, int? take, KbChangeLogService log, CancellationToken ct) =>
        {
            KbChangeTarget? parsed = null;
            if (target is not null)
            {
                if (!Enum.TryParse<KbChangeTarget>(target, true, out var t))
                    return Results.BadRequest(new { message = "target: LabAnalyteKb|MedicationKb|LabAnalyteSearchCache|MedicationSearchCache." });
                parsed = t;
            }

            var (items, total) = await log.ListAsync(parsed, targetId, Math.Max(skip ?? 0, 0), Math.Clamp(take ?? DefaultTake, 1, MaxTake), ct);
            return Results.Ok(new { items, total });
        });

        // Деталь записи — снимки «было/стало» целиком (в списке их нет: они тяжёлые).
        group.MapGet("/{id:guid}", async (Guid id, KbChangeLogService log, CancellationToken ct) =>
        {
            var detail = await log.GetDetailAsync(id, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        group.MapPost("/{id:guid}/revert", async (Guid id, AdminCatalogService admin, IMemoryCache cache, CancellationToken ct) =>
        {
            var result = await admin.RevertAsync(id, ct);
            cache.Remove(AdminPipelineEndpoints.AttentionCacheKey);
            return result switch
            {
                AdminRevertResult.Ok => Results.NoContent(),
                AdminRevertResult.NotFound => Results.NotFound(),
                AdminRevertResult.AlreadyReverted => Results.Json(
                    new { code = "already_reverted", message = "Эта запись журнала уже откачена." }, statusCode: StatusCodes.Status409Conflict),
                AdminRevertResult.CannotRevertRevert => Results.Json(
                    new { code = "cannot_revert_revert", message = "Откат нельзя откатить — выберите нужную версию в истории." },
                    statusCode: StatusCodes.Status409Conflict),
                AdminRevertResult.Conflict => Results.Json(
                    new { code = "conflict", message = "На месте записи уже появилась другая — откат невозможен." },
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.Json(
                    new { code = "nothing_to_revert", message = "В этой записи журнала нечего откатывать." },
                    statusCode: StatusCodes.Status409Conflict),
            };
        });
    }
}
