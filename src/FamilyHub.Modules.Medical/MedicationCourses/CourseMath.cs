using FamilyHub.Domain.Entities;
using FamilyHub.Domain.MedicationCourses;

namespace FamilyHub.Modules.Medical.MedicationCourses;

/// <summary>Общие расчёты по курсу для карточки, списка и «Сегодня».</summary>
internal static class CourseMath
{
    /// <summary>Какой по счёту сегодня день курса в его часовом поясе (0 — курс ещё не начался).</summary>
    public static int DayNumber(MedicationCourse c, DateTime nowUtc)
    {
        var today = DoseScheduleExpander.LocalDate(nowUtc, TimeZones.Resolve(c.TimeZoneId));
        return Math.Max(0, today.DayNumber - c.StartDate.DayNumber + 1);
    }

    public static int? TotalDays(MedicationCourse c) =>
        c.EndDate is { } end ? Math.Max(1, end.DayNumber - c.StartDate.DayNumber + 1) : null;

    public static DayPeriod PeriodOf(TimeOnly localTime) =>
        localTime.Hour < 12 ? DayPeriod.Morning : localTime.Hour < 18 ? DayPeriod.Day : DayPeriod.Evening;

    /// <summary>Сколько дней курса осталось считая сегодняшний; null — бессрочный.</summary>
    public static int? DaysLeft(MedicationCourse c, DateTime nowUtc)
    {
        if (c.EndDate is not { } end) return null;
        var today = DoseScheduleExpander.LocalDate(nowUtc, TimeZones.Resolve(c.TimeZoneId));
        return Math.Max(0, end.DayNumber - today.DayNumber + 1);
    }

    /// <summary>Итог приёма-строки для статистики и истории.</summary>
    public static DoseOutcome Outcome(MedicationDose d, MedicationCourse c, DateTime nowUtc) =>
        DoseTiming.Classify(d.Status, d.ScheduledAt ?? d.TakenAt ?? nowUtc, d.TakenAt, c.MissedAfterMinutes, d.SnoozedUntil, nowUtc);

    public static AdherenceDto Adherence(IEnumerable<DoseOutcome> outcomes)
    {
        var list = outcomes.ToList();
        var onTime = list.Count(o => o == DoseOutcome.OnTime);
        var counted = list.Count(o => o is DoseOutcome.OnTime or DoseOutcome.Late or DoseOutcome.Missed);
        return new AdherenceDto(onTime, counted, DoseTiming.OnTimePercent(list));
    }
}
