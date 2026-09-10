namespace FamilyHub.Api.Features.Admin;

/// <summary>Одна причина отказа, агрегированная по всем четырём конвейерам — карточка в
/// «Требует внимания». Reason — строка EnrichmentFailureReason (null у задач, упавших до этой
/// правки, представлен как "Unclassified" — см. AdminAttentionService).</summary>
public record AttentionReasonDto(string Reason, string Label, int Count, Dictionary<string, int> ByType);

/// <summary>Домен, чаще всего отбрасываемый фильтром — «эти N доменов разблокируют M задач».
/// JobCount — число РАЗНЫХ Failed-задач (NoTrustedSnippets), у которых этот домен встретился среди
/// сниппетов кэша и ещё не проходит EnrichmentSnippetFilter.IsEnabled.</summary>
public record DroppedDomainDto(string Domain, string Topic, int JobCount, string SampleUrl);

public record AdminAttentionDto(List<AttentionReasonDto> Reasons, List<DroppedDomainDto> DroppedDomains);

/// <summary>Topic — числом (см. WebSearchTopic, конвенция запросов админки). Добавляет все домены
/// в доверенные для этой темы и перезапускает все Failed-задачи с NoTrustedSnippets этой темы.</summary>
public record TrustAndRetryRequest(FamilyHub.Domain.Enums.WebSearchTopic Topic, List<string> Domains);

public record TrustAndRetryResponse(int RetriedCount);
