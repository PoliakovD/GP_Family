using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Задача обогащения справочника показателей (ветка medicalrecords) — зеркало
/// <see cref="MedicationEnrichmentJob"/>, отдельная таблица (а не переиспользование той же),
/// потому что дедуп-индекс по (NormalizedName, Specimen) должен быть независим между двумя
/// справочниками: показатель "натрий" и медикамент с тем же нормализованным именем не должны
/// конкурировать за один индекс. Наружу конвейер отправляет NormalizedName+Specimen — см.
/// LabAnalyteEnrichmentProcessor.
/// </summary>
public class LabAnalyteEnrichmentJob
{
    public Guid Id { get; set; }

    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>Биоматериал — вторая половина ключа дедупликации (пересборка enrich-пайплайна),
    /// та же пара, что у GlobalLabAnalyteKb.</summary>
    public SpecimenType Specimen { get; set; } = SpecimenType.Unknown;

    public string SourceDisplayName { get; set; } = string.Empty;

    /// <summary>true — принудительное переобогащение уже существующей KB-записи (см.
    /// LabAnalyteKbReenrichJob): LabAnalyteEnrichmentProcessor обычно считает Hit в справочнике
    /// поводом сразу завершить задачу Completed без внешнего запроса — Force это пропускает.</summary>
    public bool Force { get; set; }

    /// <summary>Показатель, из-за которого создана задача — справочно, не FK (LabIndicators может измениться/исчезнуть).</summary>
    public Guid? LabIndicatorId { get; set; }

    public Guid RequestedByUserId { get; set; }

    public EnrichmentJobStatus Status { get; set; } = EnrichmentJobStatus.Pending;

    public int Attempts { get; set; }

    public string? Error { get; set; }

    public string? Provider { get; set; }

    public DateTime? ExternalSearchAt { get; set; }

    public Guid? KbId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}
