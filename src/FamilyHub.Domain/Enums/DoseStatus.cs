namespace FamilyHub.Domain.Enums;

/// <summary>Состояние конкретного приёма (строка MedicationDose). Будущие приёмы строк не имеют.</summary>
public enum DoseStatus
{
    Pending = 0,
    Snoozed = 1,
    Taken = 2,

    /// <summary>Осознанный отказ пользователя — семье о нём не сообщаем.</summary>
    Skipped = 3,

    /// <summary>Срок вышел, приём не отмечен.</summary>
    Missed = 4,
}
