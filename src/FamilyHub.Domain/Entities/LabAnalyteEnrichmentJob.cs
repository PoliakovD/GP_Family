using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Задача обогащения справочника показателей (ветка medicalrecords) — зеркало
/// <see cref="MedicationEnrichmentJob"/>, отдельная таблица (а не переиспользование той же),
/// потому что дедуп-индекс по (NormalizedName, SpecimenKbId) должен быть независим между двумя
/// справочниками: показатель "натрий" и медикамент с тем же нормализованным именем не должны
/// конкурировать за один индекс. Наружу конвейер отправляет NormalizedName+SpecimenKbId — см.
/// LabAnalyteEnrichmentProcessor. Ставится только для источника, подтверждённого SpecimenResolver
/// выше порога уверенности (см. LabAnalyteEnrichmentRequestService) — SpecimenKbId никогда не
/// равен SpecimenContextIds.Unresolved.
/// </summary>
public class LabAnalyteEnrichmentJob : IReviewableEnrichmentJob
{
    public Guid Id { get; set; }

    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>Источник показателя — вторая половина ключа дедупликации (пересборка
    /// enrich-пайплайна), та же пара, что у GlobalLabAnalyteKb.</summary>
    public Guid SpecimenKbId { get; set; }

    public string SourceDisplayName { get; set; } = string.Empty;

    /// <summary>true — принудительное переобогащение уже существующей KB-записи (см.
    /// LabAnalyteKbReenrichJob): LabAnalyteEnrichmentProcessor обычно считает Hit в справочнике
    /// поводом сразу завершить задачу Completed без внешнего запроса — Force это пропускает.</summary>
    public bool Force { get; set; }

    /// <summary>Показатель, из-за которого создана задача — справочно, не FK (LabIndicators может измениться/исчезнуть).</summary>
    public Guid? LabIndicatorId { get; set; }

    public Guid RequestedByUserId { get; set; }

    /// <summary>Откуда пришёл запрос (см. EnrichmentRequestOrigin) — определяет, проходит ли
    /// задача дополнительный гейт правдоподобности в LabAnalyteEnrichmentProcessor (только
    /// ManualEntry). Дефолт Extraction — обратная совместимость существующих вызовов.</summary>
    public EnrichmentRequestOrigin Origin { get; set; } = EnrichmentRequestOrigin.Extraction;

    public EnrichmentJobStatus Status { get; set; } = EnrichmentJobStatus.Pending;

    public int Attempts { get; set; }

    public string? Error { get; set; }

    /// <summary>Машиночитаемая классификация Error (см. EnrichmentFailureReason) — null, пока
    /// задача не падала. Используется админкой для группировки падений («Требует внимания») без
    /// парсинга свободного текста.</summary>
    public EnrichmentFailureReason? FailureReason { get; set; }

    /// <summary>См. MedicalDocumentExtractionJob.IsTransientFailure — тот же смысл, тот же
    /// потребитель (LmStudioRecoverySweepJob).</summary>
    public bool IsTransientFailure { get; set; }

    public string? Provider { get; set; }

    public DateTime? ExternalSearchAt { get; set; }

    public Guid? KbId { get; set; }

    /// <summary>См. MedicalDocumentExtractionJob.CurrentThought — тот же смысл, тот же писатель
    /// (LlmThinkingReportService).</summary>
    public string? CurrentThought { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>Уверенность модели-стража (ADR-0018, этап запроса) 0..1; null — не вернула/невалидна.</summary>
    public double? QueryConfidence { get; set; }

    public string? QueryConfidenceReason { get; set; }

    /// <summary>Текст запроса для платного поиска, предложенный системой (правится админом).</summary>
    public string? ProposedQueryText { get; set; }

    /// <summary>Момент одобрения платного поиска админом (ADR-0018); null — ещё не одобрен.</summary>
    public DateTime? SearchApprovedAt { get; set; }

    /// <summary>JSON-черновик результата суммаризации, не записанный в kb (ждёт ревью).</summary>
    public string? DraftPayloadJson { get; set; }

    /// <summary>Уверенность суммаризатора (ADR-0018, этап результата) 0..1; null — не вернул/невалидна.</summary>
    public double? ResultConfidence { get; set; }

    public string? ResultConfidenceReason { get; set; }

    public Guid? ReviewedByUserId { get; set; }

    public DateTime? ReviewedAt { get; set; }
}
