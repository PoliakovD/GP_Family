using Microsoft.Extensions.Caching.Memory;

namespace FamilyHub.Api.Features.Admin;

/// <summary>
/// Очередь «Одобрение» (ADR-0018) — ручное одобрение платных поисков и результатов обогащения
/// с уверенностью ниже порога, плюс настройка порогов. Все действия сбрасывают кэш «Требует
/// внимания» (AdminPipelineEndpoints.AttentionCacheKey) — счётчик в меню не должен отставать на
/// 60 секунд от только что сделанного клика. Коды ответов: 404 — нет такой задачи/вид; 409 —
/// задача уже обработана (другой вкладкой/админом); 400 — невалидный вход; 502 — модель не ответила.
/// </summary>
public static class AdminEnrichmentReviewEndpoints
{
    private const int MaxBulkItems = 100;
    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 500;

    public static void MapAdminEnrichmentReviewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/review").RequireAuthorization("PlatformAdmin");

        group.MapGet("/counts", async (AdminEnrichmentReviewService review, CancellationToken ct) =>
            Results.Ok(await review.GetCountsAsync(ct)));

        // --- Поиски (гейт 1) ---

        group.MapGet("/searches", async (
            string? kind, int? skip, int? take, AdminEnrichmentReviewService review, CancellationToken ct) =>
        {
            if (kind is not null && !ReviewKinds.IsValid(kind)) return BadKind();
            return Results.Ok(await review.ListSearchesAsync(kind, Math.Max(skip ?? 0, 0), PageSize(take), ct));
        });

        group.MapPost("/searches/{kind}/{id:guid}/approve", async (
            string kind, Guid id, ApproveSearchRequest? request, AdminEnrichmentReviewService review,
            IMemoryCache cache, CancellationToken ct) =>
        {
            if (!ReviewKinds.IsValid(kind)) return BadKind();
            return Respond(await review.ApproveSearchAsync(kind, id, request?.QueryText, ct), cache);
        });

        group.MapPost("/searches/{kind}/{id:guid}/reject", async (
            string kind, Guid id, RejectRequest? request, AdminEnrichmentReviewService review,
            IMemoryCache cache, CancellationToken ct) =>
        {
            if (!ReviewKinds.IsValid(kind)) return BadKind();
            return Respond(await review.RejectSearchAsync(kind, id, request?.Reason, ct), cache);
        });

        group.MapPost("/searches/bulk-approve", async (
            BulkApproveSearchesRequest request, AdminEnrichmentReviewService review, IMemoryCache cache, CancellationToken ct) =>
        {
            if (request.Items.Count == 0) return Results.BadRequest(new { message = "items не может быть пустым." });
            if (request.Items.Count > MaxBulkItems)
                return Results.BadRequest(new { message = $"не более {MaxBulkItems} задач за один запрос." });
            var response = await review.BulkApproveSearchesAsync(request.Items, ct);
            if (response.ProcessedCount > 0) cache.Remove(AdminPipelineEndpoints.AttentionCacheKey);
            return Results.Ok(response);
        });

        group.MapPost("/searches/bulk-reject", async (
            BulkRejectRequest request, AdminEnrichmentReviewService review, IMemoryCache cache, CancellationToken ct) =>
        {
            if (request.Items.Count == 0) return Results.BadRequest(new { message = "items не может быть пустым." });
            if (request.Items.Count > MaxBulkItems)
                return Results.BadRequest(new { message = $"не более {MaxBulkItems} задач за один запрос." });
            var response = await review.BulkRejectSearchesAsync(request.Items, request.Reason, ct);
            if (response.ProcessedCount > 0) cache.Remove(AdminPipelineEndpoints.AttentionCacheKey);
            return Results.Ok(response);
        });

        // --- Результаты (гейт 2) ---

        group.MapGet("/results", async (
            string? kind, int? skip, int? take, AdminEnrichmentReviewService review, CancellationToken ct) =>
        {
            if (kind is not null && !ReviewKinds.IsValid(kind)) return BadKind();
            return Results.Ok(await review.ListResultsAsync(kind, Math.Max(skip ?? 0, 0), PageSize(take), ct));
        });

        group.MapGet("/results/{kind}/{id:guid}", async (
            string kind, Guid id, AdminEnrichmentReviewService review, CancellationToken ct) =>
        {
            if (!ReviewKinds.IsValid(kind)) return BadKind();
            var detail = await review.GetResultAsync(kind, id, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        group.MapPost("/results/{kind}/{id:guid}/approve", async (
            string kind, Guid id, ApproveResultRequest? request, AdminEnrichmentReviewService review,
            IMemoryCache cache, CancellationToken ct) =>
        {
            if (!ReviewKinds.IsValid(kind)) return BadKind();
            return Respond(await review.ApproveResultAsync(kind, id, request ?? new ApproveResultRequest(null, null, null), ct), cache);
        });

        group.MapPost("/results/{kind}/{id:guid}/reject", async (
            string kind, Guid id, RejectRequest? request, AdminEnrichmentReviewService review,
            IMemoryCache cache, CancellationToken ct) =>
        {
            if (!ReviewKinds.IsValid(kind)) return BadKind();
            return Respond(await review.RejectResultAsync(kind, id, request?.Reason, ct), cache);
        });

        group.MapPost("/results/{kind}/{id:guid}/resummarize", async (
            string kind, Guid id, AdminEnrichmentReviewService review, IMemoryCache cache, CancellationToken ct) =>
        {
            if (!ReviewKinds.IsValid(kind)) return BadKind();
            return Respond(await review.ResummarizeAsync(kind, id, ct), cache);
        });

        // --- Пороги уверенности ---

        group.MapGet("/config", async (AdminEnrichmentReviewService review, CancellationToken ct) =>
            Results.Ok(await review.GetConfigAsync(ct)));

        group.MapPut("/config", async (
            SetEnrichmentReviewConfigRequest request, AdminEnrichmentReviewService review, CancellationToken ct) =>
        {
            if (!await review.SetConfigAsync(request, ct))
                return Results.BadRequest(new { code = "out_of_range", message = "Порог уверенности должен быть в диапазоне 0..1." });
            return Results.Ok(await review.GetConfigAsync(ct));
        });
    }

    private static int PageSize(int? take) => Math.Clamp(take ?? DefaultPageSize, 1, MaxPageSize);

    private static IResult BadKind() =>
        Results.BadRequest(new { message = "kind должен быть lab-analyte|medication|visit-medication." });

    private static IResult Respond(ReviewActionOutcome outcome, IMemoryCache cache)
    {
        if (outcome.Result == ReviewActionResult.Ok) cache.Remove(AdminPipelineEndpoints.AttentionCacheKey);

        return outcome.Result switch
        {
            ReviewActionResult.Ok => Results.NoContent(),
            ReviewActionResult.NotFound => Results.NotFound(),
            ReviewActionResult.WrongStatus => Results.Json(
                new { code = "wrong_status", message = outcome.Message ?? "Задача уже обработана." },
                statusCode: StatusCodes.Status409Conflict),
            ReviewActionResult.UpstreamFailed => Results.Json(
                new { code = "upstream_failed", message = outcome.Message ?? "Модель не ответила." },
                statusCode: StatusCodes.Status502BadGateway),
            _ => Results.BadRequest(new { code = "invalid", message = outcome.Message ?? "Некорректный запрос." }),
        };
    }
}
