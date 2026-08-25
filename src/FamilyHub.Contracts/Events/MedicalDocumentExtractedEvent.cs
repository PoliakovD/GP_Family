namespace FamilyHub.Contracts.Events;

/// <summary>
/// Распознавание вложения завершено (ветка medicalrecords, задачи 5.2/5.3): OCR/текстовый разбор →
/// показатели/заключение → сохранение. Публикует MedicalDocumentExtractionProcessor; хендлер
/// Notifications уведомляет только владельца записи (медзапись — персональный ресурс, не семейный).
/// Только счётчики, ни имён показателей, ни значений — топики Kafka живут 7 дней
/// (KAFKA_LOG_RETENTION_HOURS), значения показателей туда попадать не должны (см.
/// .claude/patterns/backend.md, п.7 чек-листа нового доменного события).
/// </summary>
public record MedicalDocumentExtractedEvent(
    Guid JobId,
    Guid RecordId,
    Guid OwnerUserId,
    int IndicatorCount,
    int DeviationCount);
