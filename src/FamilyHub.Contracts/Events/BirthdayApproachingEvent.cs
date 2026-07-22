namespace FamilyHub.Contracts.Events;

/// <summary>
/// Приближается день рождения (публикует воркер ReminderScanJob). OccurrenceDate — дата
/// наступающего повтора (29 февраля в невисокосный год уже перенесено на 28-е воркером).
/// </summary>
public record BirthdayApproachingEvent(
    Guid BirthdayId,
    Guid FamilyId,
    string PersonName,
    DateOnly OccurrenceDate,
    int DaysUntil) : DomainEvent;
