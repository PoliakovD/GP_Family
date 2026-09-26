using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.MedicationCourses;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.MedicationCourses;

/// <summary>
/// Экран «Сегодня»: приёмы дня по всем видимым курсам (или одного человека), счётчики, пропуски
/// родственников, заканчивающийся запас и полоса недели. Приёмы — это плановые моменты из расписания
/// плюс уже существующие строки (отметки, пропуски); для каждого итог считает DoseTiming.
/// </summary>
public class MedicationTodayService(
    AppDbContext db, MedicationCourseAccess courseAccess, CourseSubjects subjects, MedkitStockService stock)
{
    /// <summary>Плановый приём курса вместе со строкой (если по нему уже что-то было).</summary>
    private sealed record Entry(
        MedicationCourse Course, DoseSchedule Schedule, DateTime ScheduledAt, TimeOnly LocalTime,
        decimal Units, MedicationDose? Row, DoseOutcome Outcome);

    public async Task<TodayResponse> GetTodayAsync(
        Guid userId, DateOnly? date, string? subjectFilter, CancellationToken ct = default)
    {
        var scope = await courseAccess.GetScopeAsync(userId, ct);
        var viewerTz = await ViewerZoneAsync(userId, ct);
        var now = DateTime.UtcNow;
        var today = DoseScheduleExpander.LocalDate(now, viewerTz);
        var day = date ?? today;

        var allCourses = await scope.Apply(db.MedicationCourses.AsNoTracking())
            .Where(c => c.Status == MedicationCourseStatus.Active).ToListAsync(ct);
        var people = await subjects.ResolveAsync(allCourses, userId, ct);
        allCourses = allCourses.Where(c => CourseSubjects.Of(people, c) is not null).ToList();
        var chips = allCourses.Select(c => CourseSubjects.Of(people, c)).Where(s => s is not null).Select(s => s!)
            .DistinctBy(s => s.Id).OrderByDescending(s => s.IsSelf).ThenBy(s => s.Name).ToList();

        var courses = FilterBySubject(allCourses, subjectFilter, userId);

        // Полоса недели и «Сегодня» строятся из одного набора — неделя с понедельника.
        var weekStart = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
        var weekStartUtc = DoseScheduleExpander.ToUtc(weekStart, TimeOnly.MinValue, viewerTz);
        var weekEndUtc = DoseScheduleExpander.ToUtc(weekStart.AddDays(7), TimeOnly.MinValue, viewerTz);
        var dayStartUtc = DoseScheduleExpander.ToUtc(day, TimeOnly.MinValue, viewerTz);
        var dayEndUtc = DoseScheduleExpander.ToUtc(day.AddDays(1), TimeOnly.MinValue, viewerTz);

        var entries = await CollectAsync(courses, weekStartUtc, weekEndUtc, now, ct);
        var todays = entries.Where(e => e.ScheduledAt >= dayStartUtc && e.ScheduledAt < dayEndUtc).ToList();

        var items = todays.Select(e =>
        {
            var access = scope.AccessTo(e.Course);
            return new TodayDoseDto(
                e.Course.Id, e.Row?.Id, e.ScheduledAt, e.LocalTime.ToString("HH:mm"), CourseMath.PeriodOf(e.LocalTime),
                e.Course.DrugName, e.Units, e.Course.DoseUnit, e.Course.Food, CourseSubjects.Of(people, e.Course)!,
                e.Outcome, e.Row?.TakenAt, e.Row?.SnoozedUntil,
                CourseMath.DayNumber(e.Course, e.ScheduledAt), CourseMath.TotalDays(e.Course),
                CanAct: access == CourseAccess.Full, IsWatching: access == CourseAccess.Watch);
        }).ToList();

        var counters = new TodayCounters(
            Taken: todays.Count(e => e.Outcome is DoseOutcome.OnTime or DoseOutcome.Late),
            Missed: todays.Count(e => e.Outcome == DoseOutcome.Missed),
            Upcoming: todays.Count(e => e.Outcome is DoseOutcome.Upcoming or DoseOutcome.Due),
            Skipped: todays.Count(e => e.Outcome == DoseOutcome.Skipped),
            Total: todays.Count,
            NextAt: todays.Where(e => e.Outcome is DoseOutcome.Upcoming or DoseOutcome.Due)
                .Select(e => (DateTime?)e.ScheduledAt).Min());

        var alerts = todays
            .Where(e => e.Outcome == DoseOutcome.Missed && CourseSubjects.Of(people, e.Course) is { IsSelf: false })
            .Select(e => new FamilyAlertDto(e.Course.Id, e.Row?.Id, CourseSubjects.Of(people, e.Course)!,
                e.Course.DrugName, e.ScheduledAt, e.LocalTime.ToString("HH:mm")))
            .ToList();

        var week = Enumerable.Range(0, 7).Select(i =>
        {
            var d = weekStart.AddDays(i);
            var dayEntries = entries.Where(e => DoseScheduleExpander.LocalDate(e.ScheduledAt, viewerTz) == d).ToList();
            return new WeekDayDto(d,
                dayEntries.Count(e => e.Outcome == DoseOutcome.OnTime), dayEntries.Count(e => e.Outcome == DoseOutcome.Late),
                dayEntries.Count(e => e.Outcome == DoseOutcome.Missed), dayEntries.Count(e => e.Outcome == DoseOutcome.Skipped),
                dayEntries.Count(e => e.Outcome is DoseOutcome.Upcoming or DoseOutcome.Due), d == today);
        }).ToList();

        var asNeeded = await AsNeededAsync(courses, people, scope, dayStartUtc, dayEndUtc, ct);
        var lowStock = await LowStockAsync(courses, scope, now, ct);

        return new TodayResponse(day, viewerTz.Id, counters, chips, items, asNeeded, alerts, lowStock, week,
            DoseTiming.OnTimePercent(entries.Select(e => e.Outcome)));
    }

    /// <summary>Сколько приёмов сегодня требуют внимания: наступили и не отмечены, либо пропущены. Только по
    /// курсам, где пользователь может действовать, — для бейджа в меню.</summary>
    public async Task<int> GetAttentionCountAsync(Guid userId, CancellationToken ct = default)
    {
        var scope = await courseAccess.GetScopeAsync(userId, ct);
        var viewerTz = await ViewerZoneAsync(userId, ct);
        var now = DateTime.UtcNow;
        var today = DoseScheduleExpander.LocalDate(now, viewerTz);

        var courses = await scope.Apply(db.MedicationCourses.AsNoTracking())
            .Where(c => c.Status == MedicationCourseStatus.Active).ToListAsync(ct);
        courses = courses.Where(c => scope.AccessTo(c) == CourseAccess.Full).ToList();

        var from = DoseScheduleExpander.ToUtc(today, TimeOnly.MinValue, viewerTz);
        var to = DoseScheduleExpander.ToUtc(today.AddDays(1), TimeOnly.MinValue, viewerTz);
        var entries = await CollectAsync(courses, from, to, now, ct);
        return entries.Count(e => e.Outcome is DoseOutcome.Due or DoseOutcome.Missed);
    }

    private static List<MedicationCourse> FilterBySubject(List<MedicationCourse> courses, string? filter, Guid userId)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter == "all") return courses;
        if (filter == "me") return courses.Where(c => c.SubjectUserId == userId).ToList();

        if (filter.Length > 2 && Guid.TryParse(filter.AsSpan(2), out var id))
        {
            if (filter.StartsWith("u:")) return courses.Where(c => c.SubjectUserId == id).ToList();
            if (filter.StartsWith("d:")) return courses.Where(c => c.FamilyDependentId == id).ToList();
        }
        return courses;
    }

    /// <summary>Плановые приёмы и существующие строки в окне [from, to) по каждому курсу.</summary>
    private async Task<List<Entry>> CollectAsync(
        List<MedicationCourse> courses, DateTime fromUtc, DateTime toUtc, DateTime now, CancellationToken ct)
    {
        if (courses.Count == 0) return [];
        var ids = courses.Select(c => c.Id).ToList();
        var rows = await db.MedicationDoses.AsNoTracking()
            .Where(d => ids.Contains(d.CourseId) && d.ScheduledAt >= fromUtc && d.ScheduledAt < toUtc)
            .ToListAsync(ct);
        var rowsByCourse = rows.ToLookup(r => r.CourseId);

        var result = new List<Entry>();
        foreach (var c in courses)
        {
            var schedule = MedicationCourseRules.ParseSchedule(c.ScheduleJson);
            if (schedule is null) continue;
            var tz = TimeZones.Resolve(c.TimeZoneId);
            var byTime = rowsByCourse[c.Id].ToDictionary(r => r.ScheduledAt!.Value);

            // Строки — история: они остаются, даже если расписание потом поменяли.
            foreach (var row in byTime.Values)
            {
                var at = row.ScheduledAt!.Value;
                result.Add(new Entry(c, schedule, at, LocalTimeOf(at, tz), row.Units, row,
                    DoseTiming.Classify(row.Status, at, row.TakenAt, c.MissedAfterMinutes, row.SnoozedUntil, now)));
            }

            var expandFrom = c.EffectiveFromUtc > fromUtc ? c.EffectiveFromUtc : fromUtc;
            foreach (var o in DoseScheduleExpander.Expand(schedule, c.StartDate, c.EndDate, tz, expandFrom, toUtc))
            {
                if (byTime.ContainsKey(o.ScheduledAtUtc)) continue;
                result.Add(new Entry(c, schedule, o.ScheduledAtUtc, o.LocalTime, o.Units, null,
                    DoseTiming.Classify(null, o.ScheduledAtUtc, null, c.MissedAfterMinutes, null, now)));
            }
        }
        return result.OrderBy(e => e.ScheduledAt).ThenBy(e => e.Course.CreatedAt).ToList();
    }

    private async Task<List<TodayAsNeededDto>> AsNeededAsync(
        List<MedicationCourse> courses, Dictionary<Guid, SubjectDto> people, CourseScope scope,
        DateTime dayStartUtc, DateTime dayEndUtc, CancellationToken ct)
    {
        var prn = courses
            .Select(c => (Course: c, Schedule: MedicationCourseRules.ParseSchedule(c.ScheduleJson)))
            .Where(x => x.Schedule is { Mode: DoseScheduleMode.AsNeeded })
            .ToList();
        if (prn.Count == 0) return [];

        var ids = prn.Select(x => x.Course.Id).ToList();
        var takenRows = await db.MedicationDoses.AsNoTracking()
            .Where(d => ids.Contains(d.CourseId) && d.ScheduledAt == null && d.Status == DoseStatus.Taken
                        && d.TakenAt >= dayStartUtc && d.TakenAt < dayEndUtc)
            .Select(d => d.CourseId).ToListAsync(ct);

        return prn.Select(x => new TodayAsNeededDto(
            x.Course.Id, x.Course.DrugName, CourseSubjects.Of(people, x.Course)!, x.Schedule!.MaxPerDay ?? 1,
            takenRows.Count(id => id == x.Course.Id), x.Schedule.AsNeededUnits, x.Course.DoseUnit,
            CanAct: scope.AccessTo(x.Course) == CourseAccess.Full)).ToList();
    }

    private async Task<List<LowStockDto>> LowStockAsync(
        List<MedicationCourse> courses, CourseScope scope, DateTime now, CancellationToken ct)
    {
        var linked = courses.Where(c => c.WriteOffEnabled && c.MedicationId != null && scope.AccessTo(c) == CourseAccess.Full).ToList();
        var result = new List<LowStockDto>();
        var infos = new Dictionary<Guid, MedkitStockService.StockInfo>();
        foreach (var id in linked.Select(c => c.MedicationId!.Value).Distinct())
        {
            var info = await stock.GetAsync(id, ct);
            if (info is not null) infos[id] = info;
        }

        foreach (var c in linked)
        {
            var s = MedicationCourseService.StockFor(c, infos, now);
            if (s?.DaysCovered is not { } days || days > c.LowStockDays) continue;
            result.Add(new LowStockDto(c.Id, c.DrugName, s.QuantityText, days, c.LowStockDays, CourseMath.DaysLeft(c, now)));
        }
        return result.OrderBy(r => r.DaysCovered).ToList();
    }

    private async Task<TimeZoneInfo> ViewerZoneAsync(Guid userId, CancellationToken ct) =>
        TimeZones.Resolve(await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.TimeZoneId).FirstOrDefaultAsync(ct));

    private static TimeOnly LocalTimeOf(DateTime utc, TimeZoneInfo tz) =>
        TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, tz));
}
