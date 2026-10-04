using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Задача обогащения справочника медикаментов (kb.global_medications_kb) для препарата,
/// упомянутого в распознанном заключении врача (UX-редизайн) — зеркало
/// <see cref="LabAnalyteEnrichmentJob"/>, не <see cref="MedicationEnrichmentJob"/>: у визита к
/// врачу нет FamilyId (MedicalRecord — персональный ресурс, см. раздел 4.2 брифа), а конвейер
/// аптечки требует его для уведомления семьи. Отдельная таблица — не блок для существующего
/// семейного конвейера, дедуп-индекс по NormalizedName независим от него (тот же приём, что у
/// LabAnalyteEnrichmentJob против MedicationEnrichmentJob — разные справочники/контуры не должны
/// конкурировать за один индекс, здесь общий с MedicationEnrichmentJob СПРАВОЧНИК, но раздельные
/// ТАБЛИЦЫ ЗАДАЧ, поэтому дедуп по NormalizedName проверяется в сервисе против обеих сразу).
/// Наружу конвейер отправляет только нормализованное имя — см. VisitMedicationEnrichmentProcessor.
/// </summary>
public class VisitMedicationEnrichmentJob : IReviewableEnrichmentJob
{
    public Guid Id { get; set; }

    public string NormalizedName { get; set; } = string.Empty;

    public string SourceDisplayName { get; set; } = string.Empty;

    /// <summary>Запись, из-за которой создана задача — справочно, не FK.</summary>
    public Guid? MedicalRecordId { get; set; }

    public Guid RequestedByUserId { get; set; }

    public EnrichmentJobStatus Status { get; set; } = EnrichmentJobStatus.Pending;

    public int Attempts { get; set; }

    public string? Error { get; set; }

    /// <summary>См. LabAnalyteEnrichmentJob.FailureReason — та же машиночитаемая классификация.</summary>
    public EnrichmentFailureReason? FailureReason { get; set; }

    /// <summary>См. MedicalDocumentExtractionJob.IsTransientFailure — тот же смысл, тот же
    /// потребитель (LmStudioRecoverySweepJob). Добавлено при cleanup-рефакторинге — этот
    /// конвейер раньше был единственным из четырёх без этого поля (структурный пробел, не
    /// умышленное решение), из-за чего LmStudioRecoverySweepJob не мог резюмировать его
    /// технические сбои так же, как у остальных трёх.</summary>
    public bool IsTransientFailure { get; set; }

    public string? Provider { get; set; }

    public DateTime? ExternalSearchAt { get; set; }

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
