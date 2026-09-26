using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.MedicationCourses;
using FamilyHub.Infrastructure.Audit;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.MedicalRecords;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.MedicationCourses;

/// <summary>
/// Курсы приёма лекарств: создание (из назначения врача или вручную), правка, пауза/возобновление/
/// завершение, список, карточка с историей и «сколько хватит». Доступ — MedicationCourseAccess
/// (свой, подопечного, наблюдаемого взрослого); чужой курс неотличим от несуществующего.
/// </summary>
public class MedicationCourseService(
    AppDbContext db, MedicationCourseAccess courseAccess, CourseSubjects subjects, MedkitStockService stock,
    IMedicalAuditWriter audit, ILogger<MedicationCourseService> logger)
{
    public const int MaxCoursesPerCreator = 300;
    private const int MaxNotesLength = MedicationCourseRules.MaxNotesLength;
    private const int MaxPrescriptionTextLength = 500;
    private const int PrescriptionWindowDays = 180;
    private const int MaxPrescriptionVisits = 20;

    // ── Чтение ──────────────────────────────────────────────────────────

    /// <summary>Список курсов: «active» — идущие и приостановленные, «completed» — завершённые.</summary>
    public async Task<List<CourseSummaryDto>> ListAsync(Guid userId, bool completed, CancellationToken ct = default)
    {
        var scope = await courseAccess.GetScopeAsync(userId, ct);
        var query = scope.Apply(db.MedicationCourses.AsNoTracking());
        query = completed
            ? query.Where(c => c.Status == MedicationCourseStatus.Completed)
            : query.Where(c => c.Status != MedicationCourseStatus.Completed);

        var courses = await query.OrderByDescending(c => c.CreatedAt).ToListAsync(ct);
        return await BuildSummariesAsync(userId, scope, courses, ct);
    }

    public async Task<(CourseResult Result, CourseDetailDto? Item)> GetAsync(
        Guid userId, Guid courseId, CancellationToken ct = default)
    {
        var (course, access, scope) = await courseAccess.LoadAsync(userId, courseId, tracking: false, ct);
        if (course is null) return (CourseResult.NotFound, null);
        return (CourseResult.Success, await BuildDetailAsync(userId, scope, course, access, ct));
    }

    public async Task<(CourseResult Result, HistoryResponse? Item)> GetHistoryAsync(
        Guid userId, Guid courseId, int weeks, CancellationToken ct = default)
    {
        var (course, _, _) = await courseAccess.LoadAsync(userId, courseId, tracking: false, ct);
        if (course is null) return (CourseResult.NotFound, null);

        weeks = Math.Clamp(weeks, 1, 13);
        var schedule = MedicationCourseRules.ParseSchedule(course.ScheduleJson);
        var tz = TimeZones.Resolve(course.TimeZoneId);
        var now = DateTime.UtcNow;
        var today = DoseScheduleExpander.LocalDate(now, tz);

        // Окно — целые недели с понедельника (сетка «пн … вс»), включая оставшиеся дни текущей недели.
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var from = today.AddDays(-daysSinceMonday - 7 * (weeks - 1));
        var to = today.AddDays(6 - daysSinceMonday);
        var fromUtc = DoseScheduleExpander.ToUtc(from, TimeOnly.MinValue, tz);
        var toUtc = DoseScheduleExpander.ToUtc(to.AddDays(1), TimeOnly.MinValue, tz);

        var rows = await db.MedicationDoses.AsNoTracking()
            .Where(d => d.CourseId == courseId && d.ScheduledAt >= fromUtc && d.ScheduledAt < toUtc)
            .ToListAsync(ct);
        var byTime = rows.ToDictionary(r => r.ScheduledAt!.Value);

        var cells = rows
            .Select(r => new HistoryCellDto(
                DoseScheduleExpander.LocalDate(r.ScheduledAt!.Value, tz),
                TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(r.ScheduledAt.Value, tz)).ToString("HH:mm"),
                r.ScheduledAt.Value, CourseMath.Outcome(r, course, now), r.Id))
            .ToList();

        // Плановые приёмы без строки: будущие и те, что ещё не успела создать фоновая задача.
        if (schedule is not null)
        {
            var expandFrom = course.EffectiveFromUtc > fromUtc ? course.EffectiveFromUtc : fromUtc;
            foreach (var o in DoseScheduleExpander.Expand(schedule, course.StartDate, course.EndDate, tz, expandFrom, toUtc))
            {
                if (byTime.ContainsKey(o.ScheduledAtUtc)) continue;
                cells.Add(new HistoryCellDto(o.LocalDate, o.LocalTime.ToString("HH:mm"), o.ScheduledAtUtc,
                    DoseTiming.Classify(null, o.ScheduledAtUtc, null, course.MissedAfterMinutes, null, now), null));
            }
        }

        var adherence = await AdherenceAsync(course, now, ct);
        return (CourseResult.Success, new HistoryResponse(from, to, cells.OrderBy(c => c.ScheduledAt).ToList(), adherence));
    }

    /// <summary>Расчёт «на курс нужно N, в аптечке M — хватит на K дней» для формы, до сохранения курса.</summary>
    public async Task<(CourseResult Result, CoursePreviewResponse? Item, string? Error)> PreviewAsync(
        Guid userId, CoursePreviewRequest request, CancellationToken ct = default)
    {
        var error = MedicationCourseRules.ValidateSchedule(request.Schedule);
        if (error is not null) return (CourseResult.Invalid, null, error);

        var tz = await UserZoneAsync(userId, ct);
        var today = DoseScheduleExpander.LocalDate(DateTime.UtcNow, tz);
        var from = request.StartDate > today ? request.StartDate : today;

        var average = DoseScheduleExpander.AverageUnitsPerDay(request.Schedule);
        decimal? needed = request.EndDate is { } end
            ? DoseScheduleExpander.CountUnits(request.Schedule, request.StartDate, end, from, end)
            : null;
        var breakStart = DoseScheduleExpander.NextBreakStart(request.Schedule, request.StartDate, request.EndDate, from);

        string? medName = null, quantityText = null;
        decimal? quantity = null, shortfall = null;
        int? daysCovered = null;
        if (request.MedicationId is { } medicationId)
        {
            var info = await stock.GetAsync(medicationId, ct);
            if (info is not null && await stock.CanUseAsync(userId, info.FamilyId, ct))
            {
                medName = info.Name;
                quantityText = info.QuantityText;
                quantity = info.Quantity;
                if (quantity is { } q)
                {
                    daysCovered = StockMath.DaysCovered(q, average);
                    if (needed is { } n) shortfall = StockMath.Shortfall(n, q);
                }
            }
        }

        return (CourseResult.Success,
            new CoursePreviewResponse(average, needed, breakStart, medName, quantityText, quantity, daysCovered, shortfall), null);
    }

    /// <summary>
    /// Назначения из визитов к врачу (последние ~полгода), которые пользователь видит, с черновиком
    /// полей формы. <paramref name="dependentId"/> null — для самого пользователя. Чужие записи (запись
    /// подопечного, внесённая другим членом семьи) читаются с аудитом, как и любой просмотр чужих
    /// медданных.
    /// </summary>
    public async Task<(CourseResult Result, List<PrescriptionVisitDto> Items)> GetPrescriptionsAsync(
        Guid userId, Guid? dependentId, CancellationToken ct = default)
    {
        var query = MedicalRecordVisibility.Visible(db, userId, MedicalRecordKind.DoctorVisit);
        var since = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-PrescriptionWindowDays);
        query = query.Where(r => r.RecordDate >= since && r.ExtractedDataJson != null);

        if (dependentId is { } depId)
        {
            var scope = await courseAccess.GetScopeAsync(userId, ct);
            var familyId = await db.FamilyDependents.AsNoTracking().Where(d => d.Id == depId)
                .Select(d => (Guid?)d.FamilyId).FirstOrDefaultAsync(ct);
            if (familyId is null || !scope.FamilyIds.Contains(familyId.Value)) return (CourseResult.NotFound, []);
            query = query.Where(r => r.FamilyDependentId == depId);
        }
        else
        {
            // «Для меня»: мои записи без подопечного либо адресованные мне лично.
            query = query.Where(r => (r.OwnerUserId == userId && r.FamilyDependentId == null && r.TargetUserId == null)
                                     || r.TargetUserId == userId);
        }

        var records = await query.OrderByDescending(r => r.RecordDate).ThenByDescending(r => r.CreatedAt)
            .Take(MaxPrescriptionVisits).ToListAsync(ct);

        var result = new List<PrescriptionVisitDto>();
        foreach (var r in records)
        {
            VisitConclusion? conclusion;
            try { conclusion = JsonSerializer.Deserialize<VisitConclusion>(r.ExtractedDataJson!); }
            catch (JsonException) { continue; }

            var items = (conclusion?.PrescribedMedications ?? [])
                .Select((m, i) => new PrescriptionItemDto(i, m.Name, m.DosageInstructions,
                    PrescriptionDraftParser.Parse(m.DosageInstructions)))
                .Where(i => !string.IsNullOrWhiteSpace(i.Name))
                .ToList();
            if (items.Count == 0) continue;

            result.Add(new PrescriptionVisitDto(r.Id, r.RecordDate, r.Doctor, r.Title, r.FamilyDependentId, items));
        }

        foreach (var ownerId in records.Where(r => r.OwnerUserId != userId).Select(r => r.OwnerUserId).Distinct())
            await audit.WriteAsync(userId, MedicalAccessAction.ViewList, ownerUserId: ownerId, ct: ct);

        return (CourseResult.Success, result);
    }

    // ── Запись ──────────────────────────────────────────────────────────

    public async Task<(CourseResult Result, CourseDetailDto? Item, string? Error)> CreateAsync(
        Guid userId, CourseRequest request, CancellationToken ct = default)
    {
        var scope = await courseAccess.GetScopeAsync(userId, ct);

        Guid? familyId = null;
        if (request.DependentId is { } depId)
        {
            var dependentFamily = await db.FamilyDependents.AsNoTracking().Where(d => d.Id == depId)
                .Select(d => (Guid?)d.FamilyId).FirstOrDefaultAsync(ct);
            if (dependentFamily is null || !scope.FamilyIds.Contains(dependentFamily.Value))
                return (CourseResult.NotFound, null, null);
            familyId = dependentFamily;
        }

        var created = await db.MedicationCourses.CountAsync(c => c.CreatedByUserId == userId, ct);
        if (created >= MaxCoursesPerCreator) return (CourseResult.Invalid, null, "Слишком много курсов.");

        var tz = await UserZoneAsync(userId, ct);
        var (error, medicationFamily) = await ValidateAsync(userId, request, familyId, tz, ct);
        if (error is not null) return (CourseResult.Invalid, null, error);

        var now = DateTime.UtcNow;
        var course = new MedicationCourse
        {
            Id = Guid.NewGuid(),
            SubjectUserId = request.DependentId is null ? userId : null,
            FamilyDependentId = request.DependentId,
            FamilyId = familyId ?? medicationFamily,
            CreatedByUserId = userId,
            Status = MedicationCourseStatus.Active,
            EffectiveFromUtc = now, // приёмы «до создания курса» пропущенными не считаются
            CreatedAt = now,
        };
        Apply(course, request, tz);
        db.MedicationCourses.Add(course);

        // Создатель курса подопечного получает напоминания — кто-то же должен дать лекарство.
        if (request.DependentId is { } dependentId)
        {
            db.MedicationWatchers.Add(new MedicationWatcher
            {
                Id = Guid.NewGuid(), FamilyDependentId = dependentId, WatcherUserId = userId,
                ReceiveReminders = true, NotifyMissed = true, CreatedAt = now,
            });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (request.DependentId is not null)
        {
            // Создатель уже был наблюдателем этого подопечного (уникальный индекс) — сохраняем курс без дубля.
            db.ChangeTracker.Clear();
            db.MedicationCourses.Add(course);
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("Курс приёма {CourseId} создан пользователем {UserId}", course.Id, userId);
        var (loaded, access, freshScope) = await courseAccess.LoadAsync(userId, course.Id, tracking: false, ct);
        return (CourseResult.Success, await BuildDetailAsync(userId, freshScope, loaded!, access, ct), null);
    }

    public async Task<(CourseResult Result, CourseDetailDto? Item, string? Error)> UpdateAsync(
        Guid userId, Guid courseId, CourseRequest request, CancellationToken ct = default)
    {
        var (course, access, scope) = await courseAccess.LoadAsync(userId, courseId, tracking: true, ct);
        if (course is null) return (CourseResult.NotFound, null, null);
        if (access != CourseAccess.Full) return (CourseResult.Forbidden, null, null);
        if (course.Status == MedicationCourseStatus.Completed) return (CourseResult.Invalid, null, "Завершённый курс изменить нельзя.");
        // Для кого курс — не меняется: это другой курс.
        if (request.DependentId != course.FamilyDependentId) return (CourseResult.Invalid, null, "Нельзя сменить, для кого курс.");

        var tz = await UserZoneAsync(userId, ct);
        var (error, medicationFamily) = await ValidateAsync(userId, request, course.FamilyDependentId is null ? null : course.FamilyId, tz, ct);
        if (error is not null) return (CourseResult.Invalid, null, error);

        var oldScheduleJson = course.ScheduleJson;
        var oldStart = course.StartDate;
        Apply(course, request, tz);
        if (course.FamilyDependentId is null) course.FamilyId = medicationFamily;

        // Правка времени/дней меняет только будущее: история остаётся как была, а задним числом
        // «пропущенных» приёмов не появляется.
        var scheduleChanged = course.ScheduleJson != oldScheduleJson || course.StartDate != oldStart;
        if (scheduleChanged)
        {
            var now = DateTime.UtcNow;
            course.EffectiveFromUtc = now;
            await DropUnresolvedDosesAsync(courseId, from: now, ct);
        }

        await db.SaveChangesAsync(ct);
        var (loaded, newAccess, freshScope) = await courseAccess.LoadAsync(userId, courseId, tracking: false, ct);
        return (CourseResult.Success, await BuildDetailAsync(userId, freshScope, loaded!, newAccess, ct), null);
    }

    public async Task<CourseResult> PauseAsync(Guid userId, Guid courseId, CancellationToken ct = default)
    {
        var (course, access, _) = await courseAccess.LoadAsync(userId, courseId, tracking: true, ct);
        if (course is null) return CourseResult.NotFound;
        if (access != CourseAccess.Full) return CourseResult.Forbidden;
        if (course.Status != MedicationCourseStatus.Active) return CourseResult.Invalid;

        course.Status = MedicationCourseStatus.Paused;
        course.PausedAt = DateTime.UtcNow;
        course.UpdatedAt = DateTime.UtcNow;
        await DropUnresolvedDosesAsync(courseId, from: null, ct);
        await db.SaveChangesAsync(ct);
        return CourseResult.Success;
    }

    public async Task<CourseResult> ResumeAsync(Guid userId, Guid courseId, CancellationToken ct = default)
    {
        var (course, access, _) = await courseAccess.LoadAsync(userId, courseId, tracking: true, ct);
        if (course is null) return CourseResult.NotFound;
        if (access != CourseAccess.Full) return CourseResult.Forbidden;
        if (course.Status != MedicationCourseStatus.Paused) return CourseResult.Invalid;

        var now = DateTime.UtcNow;
        course.Status = MedicationCourseStatus.Active;
        course.PausedAt = null;
        course.EffectiveFromUtc = now; // пропущенное за время паузы не «догоняем»
        course.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return CourseResult.Success;
    }

    public async Task<CourseResult> CompleteAsync(Guid userId, Guid courseId, CancellationToken ct = default)
    {
        var (course, access, _) = await courseAccess.LoadAsync(userId, courseId, tracking: true, ct);
        if (course is null) return CourseResult.NotFound;
        if (access != CourseAccess.Full) return CourseResult.Forbidden;
        if (course.Status == MedicationCourseStatus.Completed) return CourseResult.Invalid;

        var now = DateTime.UtcNow;
        course.Status = MedicationCourseStatus.Completed;
        course.CompletedAt = now;
        course.UpdatedAt = now;
        await DropUnresolvedDosesAsync(courseId, from: null, ct);
        await db.SaveChangesAsync(ct);
        return CourseResult.Success;
    }

    public async Task<CourseResult> DeleteAsync(Guid userId, Guid courseId, CancellationToken ct = default)
    {
        var (course, access, scope) = await courseAccess.LoadAsync(userId, courseId, tracking: false, ct);
        if (course is null) return CourseResult.NotFound;
        if (access != CourseAccess.Full || !scope.CanDelete(course)) return CourseResult.Forbidden;

        // Приёмы и токены кнопок уходят каскадом; записи дневника остаются — это личная история человека.
        await db.MedicationCourses.Where(c => c.Id == courseId).ExecuteDeleteAsync(ct);
        return CourseResult.Success;
    }

    // ── Внутреннее ──────────────────────────────────────────────────────

    /// <summary>Общая проверка запроса; возвращает текст ошибки и семью привязанной аптечки.</summary>
    private async Task<(string? Error, Guid? MedicationFamilyId)> ValidateAsync(
        Guid userId, CourseRequest request, Guid? dependentFamilyId, TimeZoneInfo tz, CancellationToken ct)
    {
        var name = request.DrugName?.Trim();
        var content = new MedicationCourseContent(name, request.Schedule, request.StartDate, request.EndDate,
            request.RepeatAfterMinutes, request.MissedAfterMinutes, request.LowStockDays);
        var error = MedicationCourseRules.Validate(content);
        if (error is not null) return (error, null);

        var today = DoseScheduleExpander.LocalDate(DateTime.UtcNow, tz);
        if (request.StartDate < today.AddDays(-365) || request.StartDate > today.AddDays(365))
            return ("Дата начала курса указана неверно.", null);
        if (request.EndDate is { } end && end > request.StartDate.AddDays(3650))
            return ("Курс слишком длинный.", null);
        if (request.Notes is { Length: > MaxNotesLength }) return ("Слишком длинная заметка.", null);
        if (request.PrescriptionText is { Length: > MaxPrescriptionTextLength }) return ("Слишком длинный текст назначения.", null);

        if (request.MedicationId is { } medicationId)
        {
            var info = await stock.GetAsync(medicationId, ct);
            // Чужая аптечка неотличима от несуществующей — не подтверждаем, что препарат есть.
            if (info is null || !await stock.CanUseAsync(userId, info.FamilyId, ct))
                return ("Препарат не найден в аптечке.", null);
            if (dependentFamilyId is { } depFamily && info.FamilyId != depFamily)
                return ("Аптечка должна быть из семьи подопечного.", null);
            return (null, info.FamilyId);
        }

        if (request.WriteOff) return ("Выберите препарат в аптечке, из которого списывать.", null);
        return (null, null);
    }

    private static void Apply(MedicationCourse course, CourseRequest r, TimeZoneInfo tz)
    {
        course.DrugName = r.DrugName!.Trim();
        course.Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim();
        course.PrescriptionText = string.IsNullOrWhiteSpace(r.PrescriptionText) ? null : r.PrescriptionText.Trim();
        course.ScheduleJson = MedicationCourseRules.SerializeSchedule(r.Schedule);
        course.Food = r.Food;
        course.DoseUnit = r.Unit;
        course.StartDate = r.StartDate;
        course.EndDate = r.EndDate;
        course.TimeZoneId = tz.Id;
        // Источник (назначение врача) задаётся при создании; правка без него (карточка его не всегда видит) не затирает.
        if (r.SourceMedicalRecordId is not null)
        {
            course.SourceMedicalRecordId = r.SourceMedicalRecordId;
            course.SourcePrescriptionIndex = r.SourcePrescriptionIndex;
        }
        course.MedicationId = r.MedicationId;
        course.WriteOffEnabled = r.WriteOff && r.MedicationId is not null;
        course.RepeatAfterMinutes = r.RepeatAfterMinutes;
        course.MissedAfterMinutes = r.MissedAfterMinutes;
        course.LowStockDays = r.LowStockDays;
        course.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Убирает незакрытые приёмы (ожидают/отложены) — при паузе, завершении и смене расписания
    /// они больше не актуальны. Их напоминания в ленте помечаются прочитанными.</summary>
    private async Task DropUnresolvedDosesAsync(Guid courseId, DateTime? from, CancellationToken ct)
    {
        var query = db.MedicationDoses.Where(d =>
            d.CourseId == courseId && (d.Status == DoseStatus.Pending || d.Status == DoseStatus.Snoozed));
        if (from is { } f) query = query.Where(d => d.ScheduledAt >= f);

        var ids = await query.Select(d => d.Id).ToListAsync(ct);
        if (ids.Count == 0) return;

        var now = DateTime.UtcNow;
        await db.Notifications
            .Where(n => ids.Contains(n.RelatedEntityId) && n.Type == NotificationType.MedicationDoseDue && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true).SetProperty(n => n.ReadAt, now), ct);
        await db.MedicationDoses.Where(d => ids.Contains(d.Id)).ExecuteDeleteAsync(ct);
    }

    private async Task<TimeZoneInfo> UserZoneAsync(Guid userId, CancellationToken ct) =>
        TimeZones.Resolve(await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.TimeZoneId).FirstOrDefaultAsync(ct));

    private async Task<AdherenceDto> AdherenceAsync(MedicationCourse course, DateTime now, CancellationToken ct)
    {
        var rows = await db.MedicationDoses.AsNoTracking()
            .Where(d => d.CourseId == course.Id && d.ScheduledAt != null)
            .ToListAsync(ct);
        return CourseMath.Adherence(rows.Select(r => CourseMath.Outcome(r, course, now)));
    }

    private async Task<List<CourseSummaryDto>> BuildSummariesAsync(
        Guid userId, CourseScope scope, List<MedicationCourse> courses, CancellationToken ct)
    {
        if (courses.Count == 0) return [];
        var now = DateTime.UtcNow;
        var people = await subjects.ResolveAsync(courses, userId, ct);
        var stocks = await LoadStocksAsync(courses, ct);

        // «Пропуск сегодня» — по всем курсам одним запросом; сутки считаем по часовому поясу курса.
        var ids = courses.Select(c => c.Id).ToList();
        var missed = await db.MedicationDoses.AsNoTracking()
            .Where(d => ids.Contains(d.CourseId) && d.Status == DoseStatus.Missed && d.ScheduledAt >= now.AddHours(-36))
            .Select(d => new { d.CourseId, d.ScheduledAt })
            .ToListAsync(ct);

        var result = new List<CourseSummaryDto>();
        foreach (var c in courses)
        {
            var subject = CourseSubjects.Of(people, c);
            if (subject is null) continue;
            var tz = TimeZones.Resolve(c.TimeZoneId);
            var today = DoseScheduleExpander.LocalDate(now, tz);
            var missedToday = missed.Any(m => m.CourseId == c.Id && DoseScheduleExpander.LocalDate(m.ScheduledAt!.Value, tz) == today);
            result.Add(ToSummary(c, subject, scope, now, missedToday, StockFor(c, stocks, now)));
        }
        return result;
    }

    private static CourseSummaryDto ToSummary(
        MedicationCourse c, SubjectDto subject, CourseScope scope, DateTime now, bool missedToday, CourseStockDto? stockDto)
    {
        var access = scope.AccessTo(c);
        var schedule = MedicationCourseRules.ParseSchedule(c.ScheduleJson) ?? new DoseSchedule(DoseScheduleMode.TimesPerDay);
        return new CourseSummaryDto(
            c.Id, c.DrugName, subject, c.Status, schedule, c.Food, c.DoseUnit, c.StartDate, c.EndDate,
            CourseMath.DayNumber(c, now), CourseMath.TotalDays(c),
            CanEdit: access == CourseAccess.Full, IsWatching: access == CourseAccess.Watch,
            missedToday, c.WriteOffEnabled, stockDto);
    }

    private async Task<Dictionary<Guid, MedkitStockService.StockInfo>> LoadStocksAsync(
        IEnumerable<MedicationCourse> courses, CancellationToken ct)
    {
        var result = new Dictionary<Guid, MedkitStockService.StockInfo>();
        foreach (var id in courses.Where(c => c.MedicationId != null).Select(c => c.MedicationId!.Value).Distinct())
        {
            var info = await stock.GetAsync(id, ct);
            if (info is not null) result[id] = info;
        }
        return result;
    }

    /// <summary>Плитка «В аптечке»: остаток, на сколько дней хватит, сколько докупить до конца курса.</summary>
    internal static CourseStockDto? StockFor(
        MedicationCourse c, IReadOnlyDictionary<Guid, MedkitStockService.StockInfo> stocks, DateTime now)
    {
        if (c.MedicationId is not { } medId || !stocks.TryGetValue(medId, out var info)) return null;
        var schedule = MedicationCourseRules.ParseSchedule(c.ScheduleJson);
        if (schedule is null) return new CourseStockDto(info.MedicationId, info.Name, info.MedkitName, info.QuantityText, info.Quantity, null, null, null);

        var average = DoseScheduleExpander.AverageUnitsPerDay(schedule);
        var tz = TimeZones.Resolve(c.TimeZoneId);
        var today = DoseScheduleExpander.LocalDate(now, tz);
        var from = c.StartDate > today ? c.StartDate : today;
        decimal? needed = c.EndDate is { } end ? DoseScheduleExpander.CountUnits(schedule, c.StartDate, end, from, end) : null;

        int? days = info.Quantity is { } q ? StockMath.DaysCovered(q, average) : null;
        decimal? shortfall = info.Quantity is { } q2 && needed is { } n ? StockMath.Shortfall(n, q2) : null;
        return new CourseStockDto(info.MedicationId, info.Name, info.MedkitName, info.QuantityText, info.Quantity, days, needed, shortfall);
    }

    private async Task<CourseDetailDto> BuildDetailAsync(
        Guid userId, CourseScope scope, MedicationCourse c, CourseAccess access, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var people = await subjects.ResolveAsync([c], userId, ct);
        var subject = CourseSubjects.Of(people, c) ?? new SubjectDto(CourseSubjects.UserKind, Guid.Empty, CourseSubjects.UnknownName, false);
        var stocks = await LoadStocksAsync([c], ct);

        var tz = TimeZones.Resolve(c.TimeZoneId);
        var today = DoseScheduleExpander.LocalDate(now, tz);
        var missedToday = await db.MedicationDoses.AsNoTracking()
            .Where(d => d.CourseId == c.Id && d.Status == DoseStatus.Missed && d.ScheduledAt >= now.AddHours(-36))
            .Select(d => d.ScheduledAt).ToListAsync(ct);
        var summary = ToSummary(c, subject, scope, now,
            missedToday.Any(m => DoseScheduleExpander.LocalDate(m!.Value, tz) == today), StockFor(c, stocks, now));

        CourseSourceDto? source = null;
        if (c.SourceMedicalRecordId is { } recordId)
        {
            var record = await MedicalRecordVisibility.Visible(db, userId, MedicalRecordKind.DoctorVisit)
                .Where(r => r.Id == recordId).FirstOrDefaultAsync(ct);
            if (record is not null) source = new CourseSourceDto(record.Id, record.Doctor, record.RecordDate);
        }

        var schedule = MedicationCourseRules.ParseSchedule(c.ScheduleJson);
        var breakStart = schedule is null ? null : DoseScheduleExpander.NextBreakStart(schedule, c.StartDate, c.EndDate, today);

        var watchers = new List<WatcherDto>();
        if (access == CourseAccess.Full)
        {
            var watcherQuery = db.MedicationWatchers.AsNoTracking();
            var subjectUserId = c.SubjectUserId;
            var dependentId = c.FamilyDependentId;
            watcherQuery = subjectUserId is not null
                ? watcherQuery.Where(w => w.SubjectUserId == subjectUserId)
                : watcherQuery.Where(w => w.FamilyDependentId == dependentId);
            var rows = await watcherQuery.ToListAsync(ct);
            var names = await subjects.UserNamesAsync(rows.Select(w => w.WatcherUserId), ct);
            watchers = rows.Select(w => new WatcherDto(w.WatcherUserId,
                names.GetValueOrDefault(w.WatcherUserId, CourseSubjects.UnknownName), w.ReceiveReminders, w.NotifyMissed)).ToList();
        }

        return new CourseDetailDto(summary, c.Notes, c.PrescriptionText, source, c.MedicationId,
            c.RepeatAfterMinutes, c.MissedAfterMinutes, c.LowStockDays, c.TimeZoneId, breakStart,
            await AdherenceAsync(c, now, ct), watchers, scope.CanDelete(c));
    }
}
