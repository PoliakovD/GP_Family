using FamilyHub.Domain.Enums;
using FamilyHub.Domain.MedicationCourses;

namespace FamilyHub.Modules.Medical.MedicationCourses;

public enum CourseResult { Success, NotFound, Forbidden, Invalid }

public enum DoseResult
{
    Success,
    NotFound,
    Forbidden,
    Invalid,

    /// <summary>Действие не подходит к текущему состоянию приёма (например, отложить уже принятый).</summary>
    Conflict,

    /// <summary>«По необходимости»: превышен лимит приёмов в сутки — нужно подтверждение (force).</summary>
    OverLimit,
}

/// <summary>Кто принимает лекарство. Kind — "user" (аккаунт) или "dependent" (подопечный без аккаунта);
/// Id уникален между видами. IsSelf — это сам вызывающий (клиент подписывает «Я»).</summary>
public record SubjectDto(string Kind, Guid Id, string Name, bool IsSelf);

/// <summary>Период дня для группировки «Сегодня»: до 12:00 — утро, до 18:00 — день, дальше — вечер.</summary>
public enum DayPeriod { Morning = 0, Day = 1, Evening = 2 }

// ── Курсы ────────────────────────────────────────────────────────────────

/// <param name="DependentId">null — курс для самого пользователя; иначе — подопечный семьи.</param>
public record CourseRequest(
    Guid? DependentId,
    string? DrugName,
    DoseSchedule Schedule,
    FoodRelation Food,
    DoseUnit Unit,
    DateOnly StartDate,
    DateOnly? EndDate,
    Guid? MedicationId,
    bool WriteOff,
    int? RepeatAfterMinutes,
    int MissedAfterMinutes,
    int LowStockDays,
    Guid? SourceMedicalRecordId,
    int? SourcePrescriptionIndex,
    string? PrescriptionText,
    string? Notes);

public record CourseStockDto(
    Guid MedicationId, string MedicationName, string? MedkitName, string? QuantityText, decimal? Quantity,
    int? DaysCovered, decimal? NeededForCourse, decimal? Shortfall);

public record CourseSummaryDto(
    Guid Id, string DrugName, SubjectDto Subject, MedicationCourseStatus Status,
    DoseSchedule Schedule, FoodRelation Food, DoseUnit Unit,
    DateOnly StartDate, DateOnly? EndDate,
    int DayNumber, int? TotalDays,
    bool CanEdit, bool IsWatching, bool MissedToday, bool WriteOffEnabled,
    CourseStockDto? Stock);

public record CourseSourceDto(Guid RecordId, string? Doctor, DateOnly RecordDate);

public record AdherenceDto(int OnTime, int Counted, int? Percent);

public record WatcherDto(Guid UserId, string Name, bool ReceiveReminders, bool NotifyMissed);

public record CourseDetailDto(
    CourseSummaryDto Summary,
    string? Notes, string? PrescriptionText, CourseSourceDto? Source,
    Guid? MedicationId, int? RepeatAfterMinutes, int MissedAfterMinutes, int LowStockDays,
    string TimeZoneId, DateOnly? NextBreakStart,
    AdherenceDto Adherence, List<WatcherDto> Watchers, bool CanDelete);

public record HistoryCellDto(DateOnly Date, string Time, DateTime ScheduledAt, DoseOutcome Outcome, Guid? DoseId);

public record HistoryResponse(DateOnly From, DateOnly To, List<HistoryCellDto> Cells, AdherenceDto Adherence);

public record CoursePreviewRequest(
    DoseSchedule Schedule, DateOnly StartDate, DateOnly? EndDate, DoseUnit Unit, Guid? MedicationId);

public record CoursePreviewResponse(
    decimal AverageUnitsPerDay, decimal? NeededForCourse, DateOnly? NextBreakStart,
    string? MedicationName, string? QuantityText, decimal? Quantity, int? DaysCovered, decimal? Shortfall);

// ── Назначения врача → черновик курса ─────────────────────────────────────

public record PrescriptionItemDto(int Index, string Name, string? DosageInstructions, PrescriptionDraft Draft);

public record PrescriptionVisitDto(
    Guid RecordId, DateOnly RecordDate, string? Doctor, string? Title, Guid? DependentId, List<PrescriptionItemDto> Items);

// ── Приёмы и «Сегодня» ────────────────────────────────────────────────────

/// <param name="ScheduledAt">Плановое время приёма (UTC), как его вернул «Сегодня»; для существующей
/// строки можно передать null и задать doseId в маршруте.</param>
public record DoseActionRequest(DateTime ScheduledAt, DoseAction Action, DateTime? TakenAt);

public record PrnRequest(DateTime? TakenAt, bool Force);

public record DoseDto(
    Guid Id, DoseStatus Status, DateTime? ScheduledAt, DateTime? TakenAt, DateTime? SnoozedUntil,
    decimal Units, bool StockWrittenOff);

public record TodayCounters(int Taken, int Missed, int Upcoming, int Skipped, int Total, DateTime? NextAt);

public record TodayDoseDto(
    Guid CourseId, Guid? DoseId, DateTime ScheduledAt, string LocalTime, DayPeriod Period,
    string DrugName, decimal Units, DoseUnit Unit, FoodRelation Food, SubjectDto Subject,
    DoseOutcome Outcome, DateTime? TakenAt, DateTime? SnoozedUntil,
    int DayNumber, int? TotalDays, bool CanAct, bool IsWatching);

public record TodayAsNeededDto(
    Guid CourseId, string DrugName, SubjectDto Subject, int MaxPerDay, int TakenToday,
    decimal Units, DoseUnit Unit, bool CanAct);

public record FamilyAlertDto(
    Guid CourseId, Guid? DoseId, SubjectDto Subject, string DrugName, DateTime ScheduledAt, string LocalTime);

public record LowStockDto(
    Guid CourseId, string DrugName, string? QuantityText, int DaysCovered, int LowStockDays, int? CourseDaysLeft);

public record WeekDayDto(DateOnly Date, int OnTime, int Late, int Missed, int Skipped, int Upcoming, bool IsToday);

public record TodayResponse(
    DateOnly Date, string TimeZoneId, TodayCounters Counters, List<SubjectDto> Subjects,
    List<TodayDoseDto> Items, List<TodayAsNeededDto> AsNeeded, List<FamilyAlertDto> Alerts,
    List<LowStockDto> LowStock, List<WeekDayDto> Week, int? WeekOnTimePercent);

public record AttentionCountResponse(int Count);

// ── Напоминания и наблюдатели ─────────────────────────────────────────────

public record WatcherCandidateDto(Guid UserId, string Name, bool Enabled);

/// <summary>За кем я слежу: подопечные моих семей (можно включить «я слежу») и взрослые, которые меня
/// выбрали наблюдателем (можно только приглушить).</summary>
public record WatchingDto(
    string Kind, Guid Id, string Name, int CourseCount, bool IsWatching, bool NotifyMissed, bool ReceiveReminders);

public record ReminderSettingsResponse(
    string? TimeZoneId, TimeOnly? QuietHoursFrom, TimeOnly? QuietHoursTo,
    List<WatcherCandidateDto> MyWatchers, List<WatchingDto> Watching);

public record SetMyWatchersRequest(List<Guid>? UserIds);

public record SetWatchingRequest(bool NotifyMissed, bool ReceiveReminders);

public record QuietHoursRequest(TimeOnly? From, TimeOnly? To);
