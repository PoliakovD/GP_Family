namespace FamilyHub.Domain.Enums;

/// <summary>
/// Вид записи дневника самочувствия (см. HealthNote). Не шифруется — по нему фильтруется лента
/// прямо в SQL. Значения — часть контракта с фронтом (числа в JSON), не переупорядочивать.
/// </summary>
public enum HealthNoteKind
{
    Symptom = 0,

    /// <summary>Домашний замер: давление, пульс, вес, глюкоза, температура, SpO₂.</summary>
    Metric = 1,

    Wellbeing = 2,

    /// <summary>Приём лекарства — свободный текст, без связи с аптечкой (аптечка семейная,
    /// дневник строго личный).</summary>
    MedicationIntake = 3,

    Sleep = 4,

    /// <summary>Свободная заметка; может быть помечена «в вопросы к врачу».</summary>
    Note = 5,
}
