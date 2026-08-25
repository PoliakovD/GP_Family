namespace FamilyHub.Contracts.Events;

/// <summary>
/// Источник дня рождения (identity rework) — раньше единственным источником была ручная запись
/// Birthday; теперь ReminderScanJob сканирует ещё и User.BirthDate активных членов семьи, и
/// FamilyDependent.BirthDate. Enum объявлен здесь же, не в FamilyHub.Domain.Enums — Contracts не
/// ссылается ни на один другой проект (см. FamilyHub.Contracts.csproj), внутренний рефакторинг
/// домена не должен молча поменять формат события.
/// </summary>
public enum BirthdaySubjectKind { Manual, Member, Dependent }

/// <summary>
/// Приближается день рождения (публикует воркер ReminderScanJob). OccurrenceDate — дата
/// наступающего повтора (29 февраля в невисокосный год уже перенесено на 28-е воркером).
/// SubjectUserId — Id именинника, если это активный член семьи (SubjectKind.Member); используется
/// потребителем, чтобы исключить самого именинника из получателей — не имеет смысла для Manual/
/// Dependent (там нет своего User).
/// </summary>
public record BirthdayApproachingEvent(
    BirthdaySubjectKind SubjectKind,
    Guid SubjectId,
    Guid FamilyId,
    string PersonName,
    DateOnly OccurrenceDate,
    int DaysUntil,
    Guid? SubjectUserId);
