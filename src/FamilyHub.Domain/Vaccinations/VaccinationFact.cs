namespace FamilyHub.Domain.Vaccinations;

/// <summary>Что случилось с конкретной дозой (или, для <see cref="VaccineSeries.ClosedByDisease"/> —
/// со всей серией сразу): реально сделана, перенесена болезнью, либо пользователь явно ответил
/// «не помню» — этот ответ отличается от отсутствия записи тем, что он УЖЕ дан (UI не должен
/// продолжать спрашивать), хотя на статус графика влияет так же, как отсутствие данных.</summary>
public enum VaccinationKind
{
    Done = 0,
    HadDisease = 1,
    Unknown = 2,
}

/// <summary>Точность даты дозы — сертификаты и память часто дают только месяц или год.</summary>
public enum VaccinationDatePrecision
{
    Day = 0,
    Month = 1,
    Year = 2,
}

/// <summary>
/// Один зафиксированный факт о серии — вход калькулятора графика (<see cref="VaccinationScheduleCalculator"/>).
/// Отделён от EF-сущности <c>Vaccination</c> (Modules.Medical.Vaccinations), чтобы калькулятор
/// оставался чистой функцией без зависимости от Infrastructure — тот же приём, что
/// <c>DoseSchedule</c>/<c>MedicationCourseRules.ParseSchedule</c> у курсов приёма лекарств.
/// </summary>
public record VaccinationFact(
    Guid RecordId,
    string SeriesCode,
    /// <summary>Индекс дозы в <see cref="VaccineSeries.Doses"/>; для повторов (см.
    /// <see cref="VaccineSeries.RepeatEveryYears"/>) — Doses.Count + k (k-й повтор с нуля).
    /// Null для факта «болел(а)», отмеченного не на конкретную дозу, а на всю серию сразу.</summary>
    int? DoseIndex,
    VaccinationKind Kind,
    DateOnly? Date,
    VaccinationDatePrecision? DatePrecision);
