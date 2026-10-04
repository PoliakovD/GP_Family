using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Задача конвейера обогащения справочника (этап 4): OCR/ручной ввод → нормализация имени →
/// промах в kb.global_medications_kb → веб-поиск → суммаризация локальным Qwen → запись в kb.
/// Живёт в схеме medical (не kb!) — у неё есть персональный контекст (кто попросил, в какой
/// семье), поэтому она не может лежать рядом с обезличенным справочником (см. KbIsolationGuardTests).
/// Дедуп на уровне БД: частичный уникальный индекс по NormalizedName среди Pending/Running —
/// один и тот же препарат, сохранённый одновременно в разных семьях, порождает один внешний запрос.
/// </summary>
public class MedicationEnrichmentJob : IReviewableEnrichmentJob
{
    public Guid Id { get; set; }

    /// <summary>Нормализованное название — ключ поиска в kb и ключ дедупликации задач.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>Название препарата как ввёл/распознал пользователь — для читаемости статуса.</summary>
    public string SourceDisplayName { get; set; } = string.Empty;

    /// <summary>Медикамент, из-за сохранения которого создана задача (может исчезнуть — не FK, только справочно).</summary>
    public Guid? MedicationId { get; set; }

    /// <summary>Кто инициировал обогащение — персональный контекст, поэтому не в kb.</summary>
    public Guid RequestedByUserId { get; set; }

    public Guid FamilyId { get; set; }

    public EnrichmentJobStatus Status { get; set; } = EnrichmentJobStatus.Pending;

    public int Attempts { get; set; }

    public string? Error { get; set; }

    /// <summary>См. LabAnalyteEnrichmentJob.FailureReason — та же машиночитаемая классификация.</summary>
    public EnrichmentFailureReason? FailureReason { get; set; }

    /// <summary>См. MedicalDocumentExtractionJob.IsTransientFailure — тот же смысл, тот же
    /// потребитель (LmStudioRecoverySweepJob).</summary>
    public bool IsTransientFailure { get; set; }

    /// <summary>Провайдер внешнего поиска, фактически использованный (например, "Brave").</summary>
    public string? Provider { get; set; }

    /// <summary>Момент фактического внешнего запроса — справочно для дебага расходов (см.
    /// WebSearchCallLog — фактический источник истины для аудита платных вызовов).</summary>
    public DateTime? ExternalSearchAt { get; set; }

    /// <summary>Строка справочника, которой завершилась задача (справочно, не FK — kb не ссылается наружу и внутрь).</summary>
    public Guid? KbId { get; set; }

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

    /// <summary>Заметка админа к решению в очереди «Одобрение» (ADR-0018) — не уходит ни во внешний поиск, ни в модель.</summary>
    public string? ReviewNote { get; set; }
}
