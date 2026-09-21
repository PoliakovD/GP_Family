using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Общее ядро полей четырёх таблиц фоновых задач конвейера распознавания/обогащения
/// (<see cref="MedicalDocumentExtractionJob"/>, <see cref="LabAnalyteEnrichmentJob"/>,
/// <see cref="MedicationEnrichmentJob"/>, <see cref="VisitMedicationEnrichmentJob"/>) — сами
/// таблицы/сущности остаются раздельными (у каждой свой предметный хвост: MedicalRecordId,
/// SpecimenKbId, MedicationId — разные дедуп-ключи и связи), объединяется только то, что
/// реально одинаково у всех четырёх. Введён при cleanup-рефакторинге, чтобы код, работающий
/// ТОЛЬКО с этим общим ядром (позиция в очереди, "текущая мысль", листинг для админки), не
/// дублировался 4 раза — см. LlmQueuePositionService/LlmThinkingReportService/UserJobsService/
/// AdminPipelineEndpoints.
/// </summary>
public interface IPipelineJob
{
    Guid Id { get; }
    Guid RequestedByUserId { get; }
    EnrichmentJobStatus Status { get; set; }
    int Attempts { get; set; }
    string? Error { get; set; }
    EnrichmentFailureReason? FailureReason { get; set; }
    bool IsTransientFailure { get; set; }
    string? CurrentThought { get; set; }
    DateTime CreatedAt { get; }
    DateTime? StartedAt { get; set; }
    DateTime? CompletedAt { get; set; }
}
