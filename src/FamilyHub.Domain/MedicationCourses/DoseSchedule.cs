using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.MedicationCourses;

/// <summary>Время приёма (локальное, «настенное») и доза в единицах курса.</summary>
public record DoseTime(TimeOnly At, decimal Units);

/// <summary>
/// Расписание курса. Хранится в MedicationCourse.ScheduleJson (jsonb) и целиком описывает, в какие
/// локальные дни и время нужно принимать препарат; конкретные моменты в UTC получает
/// <see cref="DoseScheduleExpander"/>. Поля, не относящиеся к режиму, остаются null.
/// </summary>
public record DoseSchedule(
    DoseScheduleMode Mode,
    IReadOnlyList<DoseTime>? Times = null,
    int? IntervalHours = null,
    TimeOnly? IntervalStart = null,
    decimal? IntervalUnits = null,
    IReadOnlyList<DayOfWeek>? Weekdays = null,
    int? CycleOnDays = null,
    int? CycleOffDays = null,
    int? MaxPerDay = null)
{
    /// <summary>Допустимые N для «каждые N часов»: только делители суток, чтобы времена не «плыли».</summary>
    public static readonly IReadOnlyList<int> AllowedIntervalHours = [2, 3, 4, 6, 8, 12];

    /// <summary>Времена приёма в течение активного дня. Для «каждые N часов» — развёртка от времени
    /// старта в пределах суток (12 ч от 8:00 → 8:00, 20:00). Для «по необходимости» — пусто.</summary>
    public IReadOnlyList<DoseTime> DailyTimes()
    {
        switch (Mode)
        {
            case DoseScheduleMode.EveryNHours:
                if (IntervalHours is not > 0 || IntervalStart is not { } start) return [];
                var units = IntervalUnits ?? 1m;
                var result = new List<DoseTime>();
                for (var minutes = start.Hour * 60 + start.Minute; ; minutes += IntervalHours.Value * 60)
                {
                    // Берём только приёмы в пределах суток старта: при N | 24 это ровно 24/N времён.
                    if (minutes >= (start.Hour * 60 + start.Minute) + 24 * 60) break;
                    var m = minutes % (24 * 60);
                    result.Add(new DoseTime(new TimeOnly(m / 60, m % 60), units));
                }
                return result.OrderBy(t => t.At).ToList();
            case DoseScheduleMode.AsNeeded:
                return [];
            default:
                return (Times ?? []).OrderBy(t => t.At).ToList();
        }
    }

    /// <summary>Доза одного приёма «по необходимости» (плана по времени нет, доза хранится в
    /// <see cref="IntervalUnits"/>; по умолчанию одна единица).</summary>
    public decimal AsNeededUnits => IntervalUnits ?? 1m;

    /// <summary>Суммарная доза за один активный день.</summary>
    public decimal UnitsPerActiveDay() => DailyTimes().Sum(t => t.Units);
}
