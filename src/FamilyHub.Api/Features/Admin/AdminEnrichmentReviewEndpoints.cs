using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.Extraction;
using Microsoft.Extensions.Caching.Memory;

namespace FamilyHub.Api.Features.Admin;

/// <summary>
/// Очередь «Одобрение» (ADR-0018) — единый Inbox: ручное одобрение платных поисков и результатов обогащения
/// с уверенностью ниже порога, правка набора источников (ручные сниппеты, закрепление, включение), карточка
/// сущности вне очереди и настройка порогов. Все действия сбрасывают кэш «Требует внимания»
/// (AdminPipelineEndpoints.AttentionCacheKey) — счётчик в меню не должен отставать на 60 секунд от только что
/// сделанного клика. Коды ответов: 404 — нет такой задачи/вид; 409 — задача уже обработана (другой вкладкой/админом);
/// 400 — невалидный вход; 502 — модель не ответила.
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

        // --- Единый Inbox и деталь ---

        group.MapGet("/inbox", async (
            string? kind, string? stage, int? skip, int? take, AdminEnrichmentReviewService review, CancellationToken ct) =>
        {
            if (kind is not null && !ReviewKinds.IsValid(kind)) return BadKind();
            if (stage is not null && stage is not (ReviewStages.Search or ReviewStages.Result))
                return Results.BadRequest(new { message = "stage должен быть search|result." });
            return Results.Ok(await review.ListInboxAsync(kind, stage, Math.Max(skip ?? 0, 0), PageSize(take), ct));
        });

        group.MapGet("/items/{kind}/{id:guid}", async (
            string kind, Guid id, AdminEnrichmentReviewService review, CancellationToken ct) =>
        {
            if (!ReviewKinds.IsValid(kind)) return BadKind();
            var detail = await review.GetItemAsync(kind, id, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        group.MapPut("/items/{kind}/{id:guid}/note", async (
            string kind, Guid id, SetReviewNoteRequest request, AdminEnrichmentReviewService review, CancellationToken ct) =>
        {
            if (!ReviewKinds.IsValid(kind)) return BadKind();
            return Respond(await review.SetNoteAsync(kind, id, request.Note, ct), null);
        });

        // --- Набор источников (строка кэша поиска) ---

        group.MapPost("/items/{kind}/{id:guid}/cache/ensure", async (
            string kind, Guid id, AdminEnrichmentReviewService review, CancellationToken ct) =>
        {
            if (!ReviewKinds.IsValid(kind)) return BadKind();
            var outcome = await review.EnsureCacheAsync(kind, id, ct);
            return outcome.Result == ReviewActionResult.Ok ? Results.Ok(new { cacheId = outcome.Value }) : Results.NotFound();
        });

        // «Взять из готового кэша»: кандидаты (любая непустая строка кэша той же темы) и импорт выбранных сниппетов.
        group.MapGet("/items/{kind}/{id:guid}/cache-candidates", async (
            string kind, Guid id, string? query, AdminEnrichmentReviewService review, CancellationToken ct) =>
        {
            if (!ReviewKinds.IsValid(kind)) return BadKind();
            var candidates = await review.FindCacheCandidatesAsync(kind, id, query, ct);
            return candidates is null ? Results.NotFound() : Results.Ok(candidates);
        });

        group.MapPost("/items/{kind}/{id:guid}/cache/import", async (
            string kind, Guid id, ImportReviewCacheRequest request, AdminEnrichmentReviewService review, CancellationToken ct) =>
        {
            if (!ReviewKinds.IsValid(kind)) return BadKind();
            return Respond(await review.ImportCacheAsync(kind, id, request, ct));
        });

        group.MapPut("/cache/{topic}/{cacheId:guid}/snippets", async (
            string topic, Guid cacheId, EditSnippetRequest request, AdminEnrichmentReviewService review, CancellationToken ct) =>
        {
            if (!TryTopic(topic, out var t)) return BadTopic();
            return Respond(await review.EditSnippetAsync(t, cacheId, request, ct));
        });

        group.MapPost("/cache/{topic}/{cacheId:guid}/snippets", async (
            string topic, Guid cacheId, AddManualSnippetRequest request, AdminEnrichmentReviewService review, CancellationToken ct) =>
        {
            if (!TryTopic(topic, out var t)) return BadTopic();
            return Respond(await review.AddManualSnippetAsync(t, cacheId, request, ct));
        });

        group.MapDelete("/cache/{topic}/{cacheId:guid}/snippets", async (
            string topic, Guid cacheId, string url, AdminEnrichmentReviewService review, CancellationToken ct) =>
        {
            if (!TryTopic(topic, out var t)) return BadTopic();
            return Respond(await review.RemoveSnippetAsync(t, cacheId, url, ct));
        });

        group.MapPost("/cache/{topic}/{cacheId:guid}/snippets/override", async (
            string topic, Guid cacheId, SnippetOverrideRequest request, AdminEnrichmentReviewService review, CancellationToken ct) =>
        {
            if (!TryTopic(topic, out var t)) return BadTopic();
            return Respond(await review.SetSnippetOverrideAsync(t, cacheId, request.Url, request.Enabled, ct));
        });

        group.MapPost("/cache/{topic}/{cacheId:guid}/snippets/pin", async (
            string topic, Guid cacheId, SnippetPinRequest request, AdminEnrichmentReviewService review, CancellationToken ct) =>
        {
            if (!TryTopic(topic, out var t)) return BadTopic();
            return Respond(await review.SetSnippetPinnedAsync(t, cacheId, request.Url, request.Pinned, ct));
        });

        // --- Поиски (гейт 1) ---

        group.MapPost("/searches/{kind}/{id:guid}/approve", async (
            string kind, Guid id, ApproveSearchRequest? request, AdminEnrichmentReviewService review,
            IMemoryCache cache, CancellationToken ct) =>
        {
            if (!ReviewKinds.IsValid(kind)) return BadKind();
            return Respond(await review.ApproveSearchAsync(kind, id, request ?? new ApproveSearchRequest(), ct), cache);
        });

        group.MapPost("/searches/{kind}/{id:guid}/reject", async (
            string kind, Guid id, RejectRequest? request, AdminEnrichmentReviewService review,
            IMemoryCache cache, CancellationToken ct) =>
        {
            if (!ReviewKinds.IsValid(kind)) return BadKind();
            return Respond(await review.RejectSearchAsync(kind, id, request?.Reason, request?.Note, ct), cache);
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

        // «Перепроверить по справочнику»: ключи справочника — к текущему нормализатору, запаркованные поиски показателей,
        // которые там уже находятся, — закрыть. Синхронно: админ сразу видит, сколько поисков ушло из очереди.
        group.MapPost("/searches/recheck-kb", async (LabAnalyteKbRekeyJob rekey, IMemoryCache cache, CancellationToken ct) =>
        {
            var resolved = await rekey.RunAsync(ct);
            if (resolved > 0) cache.Remove(AdminPipelineEndpoints.AttentionCacheKey);
            return Results.Ok(new RecheckKbResponse(resolved));
        });

        // --- Результаты (гейт 2) ---

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
            return Respond(await review.RejectResultAsync(kind, id, request?.Reason, request?.Note, ct), cache);
        });

        group.MapPost("/results/{kind}/{id:guid}/resummarize", async (
            string kind, Guid id, AdminEnrichmentReviewService review, IMemoryCache cache, CancellationToken ct) =>
        {
            if (!ReviewKinds.IsValid(kind)) return BadKind();
            return Respond(await review.ResummarizeAsync(kind, id, ct), cache);
        });

        // --- Карточка сущности вне очереди (вариант C) ---

        group.MapGet("/entity/{kind}/{kbId:guid}", async (
            string kind, Guid kbId, AdminEnrichmentReviewService review, CancellationToken ct) =>
        {
            if (!ReviewKinds.IsValid(kind)) return BadKind();
            var entity = await review.GetEntityAsync(kind, kbId, ct);
            return entity is null ? Results.NotFound() : Results.Ok(entity);
        });

        group.MapPost("/entity/{kind}/{kbId:guid}/resummarize-preview", async (
            string kind, Guid kbId, AdminEnrichmentReviewService review, CancellationToken ct) =>
        {
            if (!ReviewKinds.IsValid(kind)) return BadKind();
            return Respond(await review.PreviewResummarizeAsync(kind, kbId, ct));
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

    private static IResult BadTopic() =>
        Results.BadRequest(new { message = "topic должен быть lab-analyte|medication." });

    private static bool TryTopic(string topic, out WebSearchTopic result)
    {
        switch (topic)
        {
            case "lab-analyte": result = WebSearchTopic.LabAnalyte; return true;
            case "medication": result = WebSearchTopic.Medication; return true;
            default: result = default; return false;
        }
    }

    private static IResult Respond(ReviewActionOutcome outcome, IMemoryCache? cache)
    {
        if (outcome.Result == ReviewActionResult.Ok) cache?.Remove(AdminPipelineEndpoints.AttentionCacheKey);
        return ToResult(outcome.Result, outcome.Message, Results.NoContent());
    }

    private static IResult Respond<T>(ReviewActionOutcome<T> outcome) =>
        ToResult(outcome.Result, outcome.Message, Results.Ok(outcome.Value));

    private static IResult ToResult(ReviewActionResult result, string? message, IResult ok) => result switch
    {
        ReviewActionResult.Ok => ok,
        ReviewActionResult.NotFound => Results.NotFound(),
        ReviewActionResult.WrongStatus => Results.Json(
            new { code = "wrong_status", message = message ?? "Задача уже обработана." },
            statusCode: StatusCodes.Status409Conflict),
        ReviewActionResult.UpstreamFailed => Results.Json(
            new { code = "upstream_failed", message = message ?? "Модель не ответила." },
            statusCode: StatusCodes.Status502BadGateway),
        _ => Results.BadRequest(new { code = "invalid", message = message ?? "Некорректный запрос." }),
    };
}
