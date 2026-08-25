using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Modules.Medical.Extraction;

public static class ExtractionEndpoints
{
    public static void MapExtractionEndpoints(this IEndpointRouteBuilder app)
    {
        var records = app.MapGroup("/api/medical-records").RequireAuthorization();

        records.MapPost("/{recordId:guid}/attachments/{attachmentId:guid}/extract", async (
            Guid recordId, Guid attachmentId, ExtractionRequestService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.RequestAsync(recordId, attachmentId, currentUser.UserId, ct);
            return result switch
            {
                ExtractionRequestResult.NotFound => Results.NotFound(),
                ExtractionRequestResult.Forbidden => Results.Forbid(),
                ExtractionRequestResult.AlreadyQueued => Results.Accepted(),
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

        indicators.MapGet("/{analyteKey}", async (
            string analyteKey, ExtractionQueryService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.GetHistoryAsync(currentUser.UserId, analyteKey, ct)));
    }

    private static IResult MapQueryResult<T>(ExtractionQueryResult result, T? item) => result switch
    {
        ExtractionQueryResult.NotFound => Results.NotFound(),
        ExtractionQueryResult.Forbidden => Results.Forbid(),
        _ => Results.Ok(item),
    };
}
