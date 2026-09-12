namespace FamilyHub.Contracts.Events;

/// <summary>
/// Обогащение справочника препаратом (этап 4) окончательно не удалось — зеркало
/// MedicationEnrichedEvent на неудачный исход. Публикует MedicationEnrichmentProcessor ТОЛЬКО
/// когда отказ не технический (IsTransientFailure == false) — та же оговорка, что у
/// MedicalDocumentExtractionFailedEvent (LM Studio недоступна резюмируется молча).
/// </summary>
public record MedicationEnrichmentFailedEvent(
    Guid JobId,
    string DisplayName,
    Guid RequestedByUserId,
    Guid FamilyId);
