using FamilyHub.Domain.Enums;
using FamilyHub.Domain.Vaccinations;

namespace FamilyHub.Modules.Medical.HealthSummary;

/// <summary>
/// Сводка для плиток хаба «Здоровье» (редизайн навигации) — строго МОИ данные, один запрос вместо
/// восьми. Каждый блок — отдельный nullable-раздел: сбой одного источника (или у пользователя просто
/// нет данных этой категории) не должен ронять всю сводку, тем же приёмом, что HomeSummaryService.
/// </summary>
public record HealthSummaryResponse(
    HealthSummaryIntake? Intake,
    HealthSummaryDiary? Diary,
    HealthSummaryRecords? Analyses,
    HealthSummaryRecords? Visits,
    HealthSummaryIndicators? Indicators,
    HealthSummaryVaccinations? Vaccinations,
    HealthSummaryMedkits? Medkits,
    HealthSummaryReports? Reports);

public record HealthSummaryNextDose(
    Guid CourseId, DateTime ScheduledAt, string LocalTime, string DrugName, decimal Units, DoseUnit Unit, bool CanAct);

/// <summary>Плитка «Приём лекарств» — квадраты статуса рисует фронт по Taken/Missed/Upcoming/Skipped/Total.</summary>
public record HealthSummaryIntake(
    int Taken, int Missed, int Upcoming, int Skipped, int Total, HealthSummaryNextDose? Next);

/// <summary>Последнее давление за 14 дней + значения для спарклайна (только систолическое —
/// той же формы, что рисует референс-макет).</summary>
public record HealthSummaryBloodPressure(decimal Value, decimal? Value2, DateTime OccurredAt, List<decimal> RecentValues);

public record HealthSummarySymptom(string? Title, int? Severity, DateTime OccurredAt);

public record HealthSummaryDiary(HealthSummaryBloodPressure? LatestBloodPressure, HealthSummarySymptom? LatestSymptom);

public record HealthSummaryLatestRecord(
    Guid Id, string? Title, DateOnly RecordDate, string? Doctor,
    int IndicatorCount, int AbnormalIndicatorCount, int? PrescriptionCount);

/// <summary>Общая форма для плиток «Анализы» и «Приёмы врача» — те же поля, что и MedicalRecordDto,
/// плюс общий счётчик (subject=me, см. MedicalRecordFilter.MineOnly).</summary>
public record HealthSummaryRecords(HealthSummaryLatestRecord? Latest, int Total);

public record HealthSummaryHighlightIndicator(
    string AnalyteKey, string DisplayName, string ValueRaw, string? Unit, IndicatorFlag Flag,
    List<decimal> RecentValues, DateOnly FirstDate, DateOnly LastDate);

/// <summary>Плитка «Показатели» — «главный» показатель: сначала вне нормы, иначе — самый свежий
/// (см. HealthSummaryService.PickHighlightAsync); TrackedCount — все мои отслеживаемые, не только он.</summary>
public record HealthSummaryIndicators(int TrackedCount, HealthSummaryHighlightIndicator? Highlight);

public record HealthSummaryDueVaccine(string SeriesName, VaccinationStatus Status, DateOnly? WindowFrom, DateOnly? WindowTo);

public record HealthSummaryLastVaccine(string Label, DateOnly? Date);

public record HealthSummaryVaccinations(HealthSummaryDueVaccine? NextDue, HealthSummaryLastVaccine? LastDone);

/// <summary>Аптечка — семейный ресурс (не «моё» в узком смысле), но плитка живёт в хабе «Здоровье»
/// по макету — те же счётчики, что и «В порядке» на Главной, по МОИМ активным семьям.</summary>
public record HealthSummaryMedkits(int Expiring, int Expired);

public record HealthSummaryReports(int ActiveLinks);
