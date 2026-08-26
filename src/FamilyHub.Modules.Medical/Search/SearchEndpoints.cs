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
            string? q, string? types, int? page, int? pageSize,
            SearchService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.SearchAsync(
                currentUser.UserId, q, ParseTypes(types), page ?? 1, pageSize ?? 15, ct)));
    }

    /// <summary>
    /// "medication,record" -> {Medication, Record}. Невалидные/пустые токены молча игнорируются
    /// (не 400 — фильтр не критичен для UX); отсутствие параметра или пустой результат
    /// SearchService трактует как «все источники» (см. SearchService.SearchAsync).
    /// </summary>
    private static HashSet<SearchResultType>? ParseTypes(string? types)
    {
        if (string.IsNullOrWhiteSpace(types)) return null;

        var result = new HashSet<SearchResultType>();
        foreach (var token in types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Enum.TryParse<SearchResultType>(token, ignoreCase: true, out var parsed))
                result.Add(parsed);

        return result;
    }
}
