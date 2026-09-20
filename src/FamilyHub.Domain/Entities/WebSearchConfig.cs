namespace FamilyHub.Domain.Entities;

/// <summary>
/// Единственная строка — глобальный вентиль платного веб-поиска (замена удалённой месячной
/// квоты, ADR-0005 §9). Отсутствие строки означает "открыт" — та же конвенция, что у
/// <see cref="LmStudioReasoningConfig"/>/<see cref="PipelineStepConfig"/>: заводить запись заранее
/// не нужно, только когда админ реально закрывает вентиль. Управляется из админки
/// (GET/PUT /api/admin/enrichment/web-search) — закрытие не отменяет задачи обогащения, а
/// переводит их платную ветку в <see cref="Enums.EnrichmentJobStatus.Deferred"/> (см.
/// IWebSearchValveService/WebSearchValveService — читается БЕЗ кеша, в отличие от
/// LmStudioReasoningConfig: пятиминутный TTL здесь означал бы до пяти минут платных вызовов
/// после нажатия «пауза», то есть ровно тот сценарий, ради которого вентиль вводится).
/// </summary>
public class WebSearchConfig
{
    public Guid Id { get; set; }

    public bool IsPaused { get; set; }

    public DateTime? PausedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>Свободный текст — почему закрыли (например, "экономим грант до конца месяца") —
    /// показывается в баннере «Требует внимания» вместе со счётчиком отложенных задач.</summary>
    public string? Note { get; set; }
}
