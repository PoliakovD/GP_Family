using FamilyHub.Domain.Enums;
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

        records.MapGet("/{recordId:guid}/conclusion", async (
            Guid recordId, ExtractionQueryService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item) = await service.GetConclusionAsync(recordId, currentUser.UserId, ct);
            return MapQueryResult(result, item);
        });

        var indicators = app.MapGroup("/api/indicators").RequireAuthorization();

        indicators.MapGet("/", async (ExtractionQueryService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.GetMyIndicatorsAsync(currentUser.UserId, ct)));

        // Specimen в маршруте (v2) — иначе история "лейкоцитов" смешала бы кровь и мочу.
        indicators.MapGet("/{analyteKey}/{specimen:int}", async (
            string analyteKey, int specimen, ExtractionQueryService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.GetHistoryAsync(currentUser.UserId, analyteKey, (SpecimenType)specimen, ct)));

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
    }

    private static IResult MapQueryResult<T>(ExtractionQueryResult result, T? item) => result switch
    {
        ExtractionQueryResult.NotFound => Results.NotFound(),
        ExtractionQueryResult.Forbidden => Results.Forbid(),
        _ => Results.Ok(item),
    };
}
