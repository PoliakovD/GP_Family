using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.MedicationCourses;

/// <summary>Один плановый приём: момент в UTC + локальные дата/время в часовом поясе курса.</summary>
public record DoseOccurrence(DateTime ScheduledAtUtc, DateOnly LocalDate, TimeOnly LocalTime, decimal Units);

/// <summary>
/// Разворачивает расписание в плановые приёмы. Времена в расписании — локальные («в 8:00»), поэтому
/// день считается в часовом поясе курса, а наружу отдаётся UTC. Будущее никогда не хранится:
/// правка расписания меняет только то, что развернётся дальше.
/// </summary>
public static class DoseScheduleExpander
{
    /// <summary>Защита от бесконечных циклов при кривых датах: столько суток максимум разворачиваем за раз.</summary>
    private const int MaxSpanDays = 3700;

    /// <summary>Приёмы с <paramref name="fromUtc"/> (включительно) по <paramref name="toUtc"/> (исключая),
    /// в границах курса [startDate, endDate] (обе даты включительно, endDate null — бессрочно).</summary>
    public static IReadOnlyList<DoseOccurrence> Expand(DoseSchedule schedule, DateOnly startDate, DateOnly? endDate,
        TimeZoneInfo tz, DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc) return [];
        var times = schedule.DailyTimes();
        if (times.Count == 0) return [];

        // Локальные сутки, которые могут содержать приёмы окна: с запасом в сутки в обе стороны из-за поясов.
        var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(AsUtc(fromUtc), tz)).AddDays(-1);
        var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(AsUtc(toUtc), tz)).AddDays(1);
        if (firstDay < startDate) firstDay = startDate;
        if (endDate is { } end && lastDay > end) lastDay = end;
        if (lastDay.DayNumber - firstDay.DayNumber > MaxSpanDays) lastDay = firstDay.AddDays(MaxSpanDays);

        var result = new List<DoseOccurrence>();
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            if (!IsActiveDay(schedule, startDate, day)) continue;
            foreach (var t in times)
            {
                var utc = ToUtc(day, t.At, tz);
                if (utc >= AsUtc(fromUtc) && utc < AsUtc(toUtc))
                    result.Add(new DoseOccurrence(utc, day, t.At, t.Units));
            }
        }
        return result.OrderBy(o => o.ScheduledAtUtc).ToList();
    }

    /// <summary>Есть ли в этот локальный день приёмы (для режимов с днями недели и циклом).</summary>
    public static bool IsActiveDay(DoseSchedule schedule, DateOnly startDate, DateOnly day) => schedule.Mode switch
    {
        DoseScheduleMode.AsNeeded => false,
        DoseScheduleMode.Weekdays => schedule.Weekdays?.Contains(day.DayOfWeek) == true,
        DoseScheduleMode.Cycle => IsCycleOnDay(schedule, startDate, day),
        _ => true,
    };

    private static bool IsCycleOnDay(DoseSchedule s, DateOnly startDate, DateOnly day)
    {
        if (s.CycleOnDays is not > 0 || s.CycleOffDays is not > 0) return false;
        var length = s.CycleOnDays.Value + s.CycleOffDays.Value;
        var pos = ((day.DayNumber - startDate.DayNumber) % length + length) % length;
        return pos < s.CycleOnDays.Value;
    }

    /// <summary>Локальные дата+время → UTC. Времени в «дыре» перевода часов (весной) не существует —
    /// сдвигаем вперёд на размер дыры; неоднозначное (осенью) берём первым вхождением.</summary>
    public static DateTime ToUtc(DateOnly day, TimeOnly time, TimeZoneInfo tz)
    {
        var local = DateTime.SpecifyKind(day.ToDateTime(time), DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(local))
        {
            var before = tz.GetUtcOffset(local.AddHours(-3));
            var after = tz.GetUtcOffset(local.AddHours(3));
            local = local.Add(after - before);
        }
        if (tz.IsAmbiguousTime(local))
        {
            var offset = tz.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }

    /// <summary>Локальная дата момента UTC в часовом поясе.</summary>
    public static DateOnly LocalDate(DateTime utc, TimeZoneInfo tz) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), tz));

    /// <summary>Дата начала ближайшего перерыва цикла: с <paramref name="from"/>, а если перерыв уже
    /// идёт — следующего. null — нет цикла или перерыв наступает после окончания курса.</summary>
    public static DateOnly? NextBreakStart(DoseSchedule schedule, DateOnly startDate, DateOnly? endDate, DateOnly from)
    {
        if (schedule.Mode != DoseScheduleMode.Cycle || schedule.CycleOnDays is not > 0 || schedule.CycleOffDays is not > 0)
            return null;
        var on = schedule.CycleOnDays.Value;
        var length = on + schedule.CycleOffDays.Value;
        var pos = ((from.DayNumber - startDate.DayNumber) % length + length) % length;
        var next = pos < on ? from.AddDays(on - pos) : from.AddDays(length - pos + on);
        return endDate is { } end && next > end ? null : next;
    }

    /// <summary>Сколько единиц препарата уйдёт за период [from, to] (локальные даты, включительно),
    /// в границах курса. Для расчёта «на курс нужно N».</summary>
    public static decimal CountUnits(DoseSchedule schedule, DateOnly startDate, DateOnly? endDate, DateOnly from, DateOnly to)
    {
        var perDay = schedule.UnitsPerActiveDay();
        if (perDay <= 0) return 0;
        if (from < startDate) from = startDate;
        if (endDate is { } end && to > end) to = end;
        if (to < from) return 0;
        if (to.DayNumber - from.DayNumber > MaxSpanDays) to = from.AddDays(MaxSpanDays);

        decimal total = 0;
        for (var day = from; day <= to; day = day.AddDays(1))
            if (IsActiveDay(schedule, startDate, day)) total += perDay;
        return total;
    }

    /// <summary>Средний расход в сутки с учётом дней недели и перерывов цикла (0 — «по необходимости»).</summary>
    public static decimal AverageUnitsPerDay(DoseSchedule schedule)
    {
        var perDay = schedule.UnitsPerActiveDay();
        return schedule.Mode switch
        {
            DoseScheduleMode.Weekdays => perDay * (schedule.Weekdays?.Distinct().Count() ?? 0) / 7m,
            DoseScheduleMode.Cycle when schedule.CycleOnDays is > 0 && schedule.CycleOffDays is > 0 =>
                perDay * schedule.CycleOnDays.Value / (schedule.CycleOnDays.Value + schedule.CycleOffDays.Value),
            DoseScheduleMode.AsNeeded => 0,
            _ => perDay,
        };
    }

    private static DateTime AsUtc(DateTime dt) => dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
}
