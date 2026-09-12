namespace FamilyHub.Contracts.Events;

/// <summary>
/// Распознавание вложения (анализ/выписка врача) окончательно не удалось (ветка medicalrecords) —
/// зеркало MedicalDocumentExtractedEvent на неудачный исход. Публикует
/// MedicalDocumentExtractionProcessor ТОЛЬКО когда отказ не технический (IsTransientFailure ==
/// false) — LM Studio недоступна сама резюмируется LmStudioRecoverySweepJob в течение 7 дней,
/// сообщать пользователю "не удалось" в этот момент было бы дезинформацией. Reason — короткий
/// текст причины (Job.Error), без деталей документа/показателей — топики Kafka живут 7 дней
/// (KAFKA_LOG_RETENTION_HOURS), медицинские значения туда попадать не должны (см.
/// .claude/patterns/backend.md, п.7 чек-листа нового доменного события).
/// IsDoctorVisit — см. MedicalDocumentExtractedEvent, тот же смысл, для того же клик-через.
/// </summary>
public record MedicalDocumentExtractionFailedEvent(
    Guid JobId,
    Guid RecordId,
    Guid OwnerUserId,
    bool IsDoctorVisit,
    string Reason);
