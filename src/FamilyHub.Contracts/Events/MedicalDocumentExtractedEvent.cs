namespace FamilyHub.Contracts.Events;

/// <summary>
/// Распознавание вложения завершено (ветка medicalrecords, задачи 5.2/5.3): OCR/текстовый разбор →
/// показатели/заключение → сохранение. Публикует MedicalDocumentExtractionProcessor; хендлер
/// Notifications уведомляет только владельца записи (медзапись — персональный ресурс, не семейный).
/// Только счётчики, ни имён показателей, ни значений — топики Kafka живут 7 дней
/// (KAFKA_LOG_RETENTION_HOURS), значения показателей туда попадать не должны (см.
/// .claude/patterns/backend.md, п.7 чек-листа нового доменного события).
/// IsDoctorVisit — анализ или посещение врача, нужен только для клик-через уведомления на
/// правильный экран (/health/records/:id vs /health/visits/:id — единая таблица MedicalRecord,
/// но разные роуты фронта, см. NotificationRelatedKind). Простой bool, не
/// FamilyHub.Domain.Enums.MedicalRecordKind — Contracts сознательно не ссылается на Domain
/// (см. комментарий в FamilyHub.Contracts.csproj), плюс значений всего два.
/// </summary>
public record MedicalDocumentExtractedEvent(
    Guid JobId,
    Guid RecordId,
    Guid OwnerUserId,
    bool IsDoctorVisit,
    int IndicatorCount,
    int DeviationCount);
