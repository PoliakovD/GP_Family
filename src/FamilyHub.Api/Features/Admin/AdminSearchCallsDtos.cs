using FamilyHub.Domain.Enums;

namespace FamilyHub.Api.Features.Admin;

/// <summary>Строка списка (`GET /api/admin/search-calls`) — без QueryText/ResultUrlsJson целиком
/// (превью), для таблицы. Полный текст запроса — только в детали (см. SearchCallDetailDto),
/// чтобы список оставался лёгким на большом периоде.</summary>
public record SearchCallRowDto(
    Guid Id, DateTime OccurredAt, string Provider, WebSearchTopic Topic, string NormalizedName,
    string? SpecimenDisplayName, int? HttpStatus, int DurationMs, WebSearchCallOutcome Outcome,
    int SnippetCount, string? JobKind, Guid? JobId);

public record SearchCallListResponse(List<SearchCallRowDto> Rows, int Total, int Page, int PageSize);

/// <summary>Полная карточка одного вызова — весь текст запроса + список URL реально
/// использованных источников (до фильтра по доверенным доменам, тот применяется процессором уже
/// после записи лога).</summary>
public record SearchCallDetailDto(
    Guid Id, DateTime OccurredAt, string Provider, WebSearchTopic Topic, string NormalizedName,
    string? SpecimenDisplayName, string QueryText, string? Endpoint, int? HttpStatus, int DurationMs,
    WebSearchCallOutcome Outcome, int SnippetCount, List<string> ResultUrls, string? Error,
    string? JobKind, Guid? JobId);

/// <summary>Разбивка за период (`GET /api/admin/search-calls/stats`) — «работает ли кэш» одним
/// числом (CacheHitShare) плюс расход текущего месяца. Квоты больше нет (ADR-0005 §9, замена
/// вентилем) — вместо неё состояние вентиля (WebSearchPaused/PausedAt) и денежная оценка
/// (EstimatedMonthlySpend = UsedThisMonth * EnrichmentOptions.PricePerPaidCall, null если цена
/// не задана).</summary>
public record SearchCallStatsDto(
    int TotalCalls, int PaidCalls, int CacheHits, double CacheHitShare,
    List<SearchCallCountByKeyDto> ByProvider, List<SearchCallCountByKeyDto> ByOutcome,
    List<SearchCallDailyCountDto> ByDay, int UsedThisMonth,
    bool WebSearchPaused, DateTime? PausedAt, decimal? EstimatedMonthlySpend);

public record SearchCallCountByKeyDto(string Key, int Count);

public record SearchCallDailyCountDto(DateOnly Day, int PaidCalls, int CacheHits);
