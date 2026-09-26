using System.Text.Json;
using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.MedicationCourses;

/// <summary>Содержимое курса без служебных полей — то, что проверяют правила.</summary>
public record MedicationCourseContent(
    string? DrugName,
    DoseSchedule Schedule,
    DateOnly StartDate,
    DateOnly? EndDate,
    int? RepeatAfterMinutes = null,
    int MissedAfterMinutes = MedicationCourseRules.DefaultMissedAfterMinutes,
    int LowStockDays = MedicationCourseRules.DefaultLowStockDays);

/// <summary>Правила курса приёма: валидация и (де)сериализация расписания. Единое место для всех клиентов.</summary>
public static class MedicationCourseRules
{
    public const int MaxDrugNameLength = 200;
    public const int MaxNotesLength = 2000;
    public const int MaxTimesPerDay = 12;
    public const decimal MinUnits = 0.25m;
    public const decimal MaxUnits = 100m;
    public const int MaxCycleDays = 90;
    public const int MaxPerDayLimit = 24;

    public const int DefaultMissedAfterMinutes = 120;
    public const int DefaultLowStockDays = 5;

    public static readonly IReadOnlyList<int> AllowedRepeatMinutes = [5, 15, 30];
    public static readonly IReadOnlyList<int> AllowedMissedAfterMinutes = [30, 60, 120, 180, 240];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>null — курс корректен; иначе сообщение об ошибке (для 400).</summary>
    public static string? Validate(MedicationCourseContent c)
    {
        if (string.IsNullOrWhiteSpace(c.DrugName)) return "Укажите название препарата.";
        if (c.DrugName.Length > MaxDrugNameLength) return "Слишком длинное название.";
        if (c.EndDate is { } end && end < c.StartDate) return "Дата окончания раньше даты начала.";
        if (c.RepeatAfterMinutes is { } r && !AllowedRepeatMinutes.Contains(r)) return "Повтор напоминания — 5, 15 или 30 минут.";
        if (!AllowedMissedAfterMinutes.Contains(c.MissedAfterMinutes)) return "Неверный порог пропуска приёма.";
        if (c.LowStockDays is < 1 or > 60) return "Запас для предупреждения — от 1 до 60 дней.";
        return ValidateSchedule(c.Schedule);
    }

    public static string? ValidateSchedule(DoseSchedule s)
    {
        if (!Enum.IsDefined(s.Mode)) return "Неизвестный режим расписания.";

        switch (s.Mode)
        {
            case DoseScheduleMode.TimesPerDay:
                return ValidateTimes(s.Times);

            case DoseScheduleMode.EveryNHours:
                if (s.IntervalHours is not { } h || !DoseSchedule.AllowedIntervalHours.Contains(h))
                    return "Интервал — 2, 3, 4, 6, 8 или 12 часов.";
                if (s.IntervalStart is null) return "Укажите время первого приёма.";
                return ValidateUnits(s.IntervalUnits ?? 1m);

            case DoseScheduleMode.Weekdays:
                if (s.Weekdays is not { Count: > 0 } days || days.Count > 7 || days.Distinct().Count() != days.Count
                    || days.Any(d => !Enum.IsDefined(d)))
                    return "Выберите дни недели.";
                return ValidateTimes(s.Times);

            case DoseScheduleMode.Cycle:
                if (s.CycleOnDays is not (>= 1 and <= MaxCycleDays) || s.CycleOffDays is not (>= 1 and <= MaxCycleDays))
                    return $"Дни приёма и перерыва — от 1 до {MaxCycleDays}.";
                return ValidateTimes(s.Times);

            case DoseScheduleMode.AsNeeded:
                return s.MaxPerDay is >= 1 and <= MaxPerDayLimit ? null : $"Лимит в сутки — от 1 до {MaxPerDayLimit}.";

            default:
                return "Неизвестный режим расписания.";
        }
    }

    private static string? ValidateTimes(IReadOnlyList<DoseTime>? times)
    {
        if (times is not { Count: > 0 }) return "Добавьте хотя бы одно время приёма.";
        if (times.Count > MaxTimesPerDay) return $"Не больше {MaxTimesPerDay} приёмов в день.";
        if (times.Select(t => t.At).Distinct().Count() != times.Count) return "Время приёма повторяется.";
        return times.Select(t => ValidateUnits(t.Units)).FirstOrDefault(e => e is not null);
    }

    private static string? ValidateUnits(decimal units) =>
        units is >= MinUnits and <= MaxUnits ? null : "Доза — от 0,25 до 100.";

    public static string SerializeSchedule(DoseSchedule schedule) => JsonSerializer.Serialize(schedule, Json);

    /// <summary>Битый JSON не должен ронять список курсов — возвращаем null, вызывающий пропускает курс.</summary>
    public static DoseSchedule? ParseSchedule(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<DoseSchedule>(json, Json); }
        catch (JsonException) { return null; }
    }
}
