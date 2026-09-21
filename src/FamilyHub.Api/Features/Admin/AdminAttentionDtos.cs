namespace FamilyHub.Api.Features.Admin;

/// <summary>Одна причина отказа, агрегированная по всем четырём конвейерам — карточка в
/// «Требует внимания». Reason — строка EnrichmentFailureReason (null у задач, упавших до этой
/// правки, представлен как "Unclassified" — см. AdminAttentionService). Count — сырое число
/// Failed-строк (может включать дубли одного названия — см. class doc *RequestService и
/// /jobs/dedupe-failed); DistinctCount — сколько РАЗНЫХ названий(+биоматериалов) за этим стоит,
/// Count &gt; DistinctCount значит в списке есть повторы одного и того же названия.</summary>
public record AttentionReasonDto(string Reason, string Label, int Count, int DistinctCount, Dictionary<string, int> ByType);

/// <summary>Домен, чаще всего отбрасываемый фильтром — «эти N доменов разблокируют M задач».
/// JobCount — число РАЗНЫХ Failed-задач (NoTrustedSnippets), у которых этот домен встретился среди
/// сниппетов кэша и ещё не проходит EnrichmentSnippetFilter.IsEnabled.</summary>
public record DroppedDomainDto(string Domain, string Topic, int JobCount, string SampleUrl);

/// <summary>Закрытый вентиль платного поиска (ADR-0005 §9) откладывает задачи молча
/// (EnrichmentJobStatus.Deferred) — без этого блока закрытый вентиль выглядел бы в инбоксе как
/// зависший конвейер: ни одной ошибки, просто ничего не движется. ByType — как у AttentionReasonDto,
/// разбивка отложенных по конвейеру ("lab-analyte"/"medication"/"visit-medication").</summary>
public record WebSearchPausedDto(bool IsPaused, DateTime? PausedAt, string? Note, int DeferredTotal, Dictionary<string, int> ByType);

public record AdminAttentionDto(List<AttentionReasonDto> Reasons, List<DroppedDomainDto> DroppedDomains, WebSearchPausedDto WebSearchPaused);

/// <summary>Topic — числом (см. WebSearchTopic, конвенция запросов админки). Добавляет все домены
/// в доверенные для этой темы и перезапускает все Failed-задачи с NoTrustedSnippets этой темы.</summary>
public record TrustAndRetryRequest(FamilyHub.Domain.Enums.WebSearchTopic Topic, List<string> Domains);

public record TrustAndRetryResponse(int RetriedCount);
