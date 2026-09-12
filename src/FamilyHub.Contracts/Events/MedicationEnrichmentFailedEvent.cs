namespace FamilyHub.Contracts.Events;

/// <summary>
/// Обогащение справочника препаратом (этап 4) окончательно не удалось — зеркало
/// MedicationEnrichedEvent на неудачный исход. Публикует MedicationEnrichmentProcessor ТОЛЬКО
/// когда отказ не технический (IsTransientFailure == false) — та же оговорка, что у
/// MedicalDocumentExtractionFailedEvent (LM Studio недоступна резюмируется молча).
/// MedkitId — аптечка, в которой лежал медикамент, запустивший обогащение (null — медикамент
/// уже удалён к моменту отказа, справочно не FK, см. MedicationEnrichmentJob.MedicationId);
/// нужен только для клик-через, экран открытой аптечки один на все медикаменты
/// (/health/medications/:medkitId, не :medicationId).
/// </summary>
public record MedicationEnrichmentFailedEvent(
    Guid JobId,
    string DisplayName,
    Guid RequestedByUserId,
    Guid FamilyId,
    Guid? MedkitId);
