using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Modules.Medical.Search;

public static class SearchEndpoints
{
    public static void MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/search").RequireAuthorization();

        // Пустой/короткий q — пустой результат без 400 (см. SearchService.MinQueryLength):
        // фронт может дергать поиск по мере набора текста без спец-обработки первых символов.
        group.MapGet("/", async (
            string? q, SearchService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.SearchAsync(currentUser.UserId, q, ct)));
    }
}
