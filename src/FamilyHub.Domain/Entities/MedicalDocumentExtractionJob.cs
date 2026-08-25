using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Задача конвейера извлечения (ветка medicalrecords, задачи 5.2/5.3) — зеркало
/// <see cref="MedicationEnrichmentJob"/>, тот же паттерн Hangfire-очереди с наблюдаемым статусом.
/// Живёт в схеме medical (персональный контекст — какая запись, кто попросил).
/// Частичный уникальный индекс по AttachmentId среди Pending/Running (см.
/// MedicalDocumentExtractionJobConfiguration) — дедуп повторного клика «Распознать» на одном
/// вложении, тот же приём, что и NormalizedName у MedicationEnrichmentJob.
/// </summary>
public class MedicalDocumentExtractionJob
{
    public Guid Id { get; set; }

    public Guid MedicalRecordId { get; set; }

    public Guid AttachmentId { get; set; }

    public Guid RequestedByUserId { get; set; }

    public EnrichmentJobStatus Status { get; set; } = EnrichmentJobStatus.Pending;

    public ExtractionStage Stage { get; set; } = ExtractionStage.Queued;

    public int Attempts { get; set; }

    /// <summary>Сколько показателей сохранено — только для отображения прогресса, не источник
    /// истины (сами показатели — в LabIndicators).</summary>
    public int IndicatorCount { get; set; }

    public string? Error { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}
