namespace FamilyHub.Contracts.Events;

/// <summary>
/// Срок годности лекарства истёк или скоро истечёт (публикует воркер ReminderScanJob).
/// Хендлер Notifications создаёт оповещения активным членам семьи и шлёт TG-алерты.
/// </summary>
public record MedicationExpiringEvent(
    Guid MedicationId,
    Guid FamilyId,
    string Name,
    DateOnly ExpiryDate,
    bool IsExpired);
