using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Modules.Medical.Extraction;

public static class ExtractionEndpoints
{
    public static void MapExtractionEndpoints(this IEndpointRouteBuilder app)
    {
        var records = app.MapGroup("/api/medical-records").RequireAuthorization();

        // v2: одна кнопка «Распознать» на ЗАПИСЬ — обрабатывает все ещё не распознанные вложения
        // последовательно (см. ExtractionRequestService/MedicalDocumentExtractionProcessor), не
        // по клику на каждый файл.
        records.MapPost("/{recordId:guid}/extract", async (
            Guid recordId, ExtractionRequestService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.RequestAsync(recordId, currentUser.UserId, ct);
            return result switch
            {
                ExtractionRequestResult.NotFound => Results.NotFound(),
                ExtractionRequestResult.Forbidden => Results.Forbid(),
                ExtractionRequestResult.NothingToDo => Results.Json(
                    new { code = "nothing_to_extract" }, statusCode: StatusCodes.Status409Conflict),
                _ => Results.Accepted(),
            };
        });

        records.MapGet("/{recordId:guid}/extraction", async (
            Guid recordId, ExtractionQueryService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item) = await service.GetStatusAsync(recordId, currentUser.UserId, ct);
            return MapQueryResult(result, item);
        });

        records.MapGet("/{recordId:guid}/indicators", async (
            Guid recordId, ExtractionQueryService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, items) = await service.GetIndicatorsAsync(recordId, currentUser.UserId, ct);
            return result switch
            {
                ExtractionQueryResult.NotFound => Results.NotFound(),
                ExtractionQueryResult.Forbidden => Results.Forbid(),
                _ => Results.Ok(items),
            };
        });

        records.MapGet("/{recordId:guid}/summary", async (
            Guid recordId, ExtractionQueryService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item) = await service.GetSummaryAsync(recordId, currentUser.UserId, ct);
            return MapQueryResult(result, item);
        });

        // Пересчёт "Резюме"/"Вопросы врачу" по текущим (в т.ч. вручную поправленным) показателям —
        // без повторного распознавания документа (см. class doc RegenerateSummaryAsync).
        records.MapPost("/{recordId:guid}/summary/regenerate", async (
            Guid recordId, ExtractionQueryService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item) = await service.RegenerateSummaryAsync(recordId, currentUser.UserId, ct);
            return result switch
            {
                ExtractionQueryResult.NotFound => Results.NotFound(),
                ExtractionQueryResult.Forbidden => Results.Forbid(),
                ExtractionQueryResult.Failed => Results.Json(
                    new { code = "summary_regeneration_failed", message = "Не удалось пересчитать резюме — локальный сервер распознавания недоступен или не смог обработать показатели." },
                    statusCode: StatusCodes.Status502BadGateway),
                _ => Results.Ok(item),
            };
        });

        records.MapGet("/{recordId:guid}/conclusion", async (
            Guid recordId, ExtractionQueryService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item) = await service.GetConclusionAsync(recordId, currentUser.UserId, ct);
            return MapQueryResult(result, item);
        });

        // Ручное добавление показателя (UX-редизайн) — без ожидания следующего «Распознать»,
        // тот же владелец-чек, что и у остальных мутаций записи.
        records.MapPost("/{recordId:guid}/indicators", async (
            Guid recordId, CreateIndicatorRequest body, ExtractionQueryService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item) = await service.CreateIndicatorAsync(recordId, currentUser.UserId, body, ct);
            return result switch
            {
                CreateIndicatorResult.NotFound => Results.NotFound(),
                CreateIndicatorResult.Forbidden => Results.Forbid(),
                CreateIndicatorResult.Conflict => Results.Json(
                    new { code = "indicator_conflict" }, statusCode: StatusCodes.Status409Conflict),
                _ => Results.Created($"/api/indicators/{item!.Id}", item),
            };
        });

        var indicators = app.MapGroup("/api/indicators").RequireAuthorization();

        indicators.MapGet("/", async (ExtractionQueryService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.GetMyIndicatorsAsync(currentUser.UserId, ct)));

        // SpecimenKbId в query (не в path, UX-редизайн) — второй ключ группировки; без него
        // история "лейкоцитов" смешала бы кровь и мочу.
        indicators.MapGet("/{analyteKey}", async (
            string analyteKey, Guid specimenKbId,
            ExtractionQueryService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.GetHistoryAsync(currentUser.UserId, analyteKey, specimenKbId, ct)));

        // Правка показателя вручную (ошибка OCR) — только владелец записи.
        indicators.MapPut("/{id:guid}", async (
            Guid id, UpdateIndicatorRequest body, ExtractionQueryService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.UpdateIndicatorAsync(id, currentUser.UserId, body, ct);
            return result switch
            {
                UpdateIndicatorResult.NotFound => Results.NotFound(),
                UpdateIndicatorResult.Forbidden => Results.Forbid(),
                UpdateIndicatorResult.Conflict => Results.Json(
                    new { code = "indicator_conflict" }, statusCode: StatusCodes.Status409Conflict),
                _ => Results.NoContent(),
            };
        });

        indicators.MapDelete("/{id:guid}", async (
            Guid id, ExtractionQueryService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.DeleteIndicatorAsync(id, currentUser.UserId, ct);
            return result switch
            {
                DeleteIndicatorResult.NotFound => Results.NotFound(),
                DeleteIndicatorResult.Forbidden => Results.Forbid(),
                _ => Results.NoContent(),
            };
        });

        // Персонализированная статья справочника (редизайн v2) — панель/шторка справки по клику
        // на строку показателя.
        indicators.MapGet("/{id:guid}/article", async (
            Guid id, ExtractionQueryService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item) = await service.GetArticleAsync(id, currentUser.UserId, ct);
            return MapQueryResult(result, item);
        });

        // Тренд показателя для КОНКРЕТНОЙ записи (редизайн v2) — в отличие от
        // GET /api/indicators/{analyteKey} выше (строго "свои"), работает и для расшаренной чужой
        // записи, с двойным фильтром видимости (см. GetRecordIndicatorHistoryAsync).
        records.MapGet("/{recordId:guid}/indicators/{indicatorId:guid}/history", async (
            Guid recordId, Guid indicatorId, ExtractionQueryService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, items) = await service.GetRecordIndicatorHistoryAsync(recordId, indicatorId, currentUser.UserId, ct);
            return result switch
            {
                ExtractionQueryResult.NotFound => Results.NotFound(),
                ExtractionQueryResult.Forbidden => Results.Forbid(),
                _ => Results.Ok(items),
            };
        });
    }

    private static IResult MapQueryResult<T>(ExtractionQueryResult result, T? item) => result switch
    {
        ExtractionQueryResult.NotFound => Results.NotFound(),
        ExtractionQueryResult.Forbidden => Results.Forbid(),
        _ => Results.Ok(item),
    };
}
