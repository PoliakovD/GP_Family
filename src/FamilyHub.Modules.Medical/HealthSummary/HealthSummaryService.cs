using FamilyHub.Domain.Enums;
using FamilyHub.Domain.MedicationCourses;
using FamilyHub.Domain.Vaccinations;
using FamilyHub.Domain.ValueObjects;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Notifications;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.DoctorReports;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.HealthNotes;
using FamilyHub.Modules.Medical.MedicalRecords;
using FamilyHub.Modules.Medical.MedicationCourses;
using FamilyHub.Modules.Medical.Vaccinations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Modules.Medical.HealthSummary;

/// <summary>
/// Собирает сводку хаба «Здоровье» (редизайн навигации) — восемь плиток одним запросом, все данные
/// строго мои (никаких подопечных/наблюдаемых — та же граница, что решает macет: "хаб — про меня").
/// Переиспользует существующие сервисы модуля один-в-один, не дублирует их запросы. Каждый блок
/// собирается независимо и превращается в null при сбое — сводка не должна падать целиком из-за
/// одной неисправной категории (тот же принцип, что HomeSummaryService для семейной сводки).
/// </summary>
public class HealthSummaryService(
    AppDbContext db,
    IFamilyAccessService access,
    IOptions<NotificationOptions> notificationOptions,
    MedicationTodayService intake,
    HealthNoteService diary,
    MedicalRecordService records,
    ExtractionQueryService indicators,
    VaccinationService vaccinations,
    DoctorReportService reports,
    ILogger<HealthSummaryService> logger)
{
    /// <summary>Сколько последних точек отдавать на спарклайн — этого достаточно для мелкой плитки,
    /// не для полноценного графика (см. полноэкранные разделы, где точек больше).</summary>
    private const int SparklinePoints = 6;

    public async Task<HealthSummaryResponse> BuildAsync(Guid userId, CancellationToken ct = default)
    {
        return new HealthSummaryResponse(
            await SafeAsync(nameof(BuildIntakeAsync), () => BuildIntakeAsync(userId, ct)),
            await SafeAsync(nameof(BuildDiaryAsync), () => BuildDiaryAsync(userId, ct)),
            await SafeAsync(nameof(BuildRecordsAsync), () => BuildRecordsAsync(userId, MedicalRecordKind.Analysis, ct)),
            await SafeAsync(nameof(BuildRecordsAsync), () => BuildRecordsAsync(userId, MedicalRecordKind.DoctorVisit, ct)),
            await SafeAsync(nameof(BuildIndicatorsAsync), () => BuildIndicatorsAsync(userId, ct)),
            await SafeAsync(nameof(BuildVaccinationsAsync), () => BuildVaccinationsAsync(userId, ct)),
            await SafeAsync(nameof(BuildMedkitsAsync), () => BuildMedkitsAsync(userId, ct)),
            await SafeAsync(nameof(BuildReportsAsync), () => BuildReportsAsync(userId, ct)));
    }

    private async Task<T?> SafeAsync<T>(string block, Func<Task<T?>> build) where T : class
    {
        try
        {
            return await build();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Сводка здоровья: блок {Block} не собран, отдаём null", block);
            return null;
        }
    }

    private async Task<HealthSummaryIntake?> BuildIntakeAsync(Guid userId, CancellationToken ct)
    {
        var today = await intake.GetTodayAsync(userId, date: null, subjectFilter: "me", ct);
        var c = today.Counters;
        if (c.Total == 0) return new HealthSummaryIntake(0, 0, 0, 0, 0, null);

        var next = today.Items
            .Where(i => i.Outcome is DoseOutcome.Upcoming or DoseOutcome.Due)
            .OrderBy(i => i.ScheduledAt)
            .Select(i => new HealthSummaryNextDose(i.CourseId, i.ScheduledAt, i.LocalTime, i.DrugName, i.Units, i.Unit, i.CanAct))
            .FirstOrDefault();

        return new HealthSummaryIntake(c.Taken, c.Missed, c.Upcoming, c.Skipped, c.Total, next);
    }

    private async Task<HealthSummaryDiary?> BuildDiaryAsync(Guid userId, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-14);
        var pressure = await diary.GetMetricSeriesAsync(userId, userId, "blood_pressure", since, null, ct);
        HealthSummaryBloodPressure? bp = null;
        if (pressure is { Count: > 0 })
        {
            var last = pressure[^1];
            var recent = pressure.TakeLast(SparklinePoints).Select(p => p.Value).ToList();
            bp = new HealthSummaryBloodPressure(last.Value, last.Value2, last.OccurredAt, recent);
        }

        var symptoms = await diary.ListAsync(userId, userId, DateTime.UtcNow.AddDays(-3), null, HealthNoteKind.Symptom, ct);
        var lastSymptom = symptoms?.OrderByDescending(n => n.OccurredAt).FirstOrDefault();
        var symptom = lastSymptom is null
            ? null
            : new HealthSummarySymptom(lastSymptom.Title, lastSymptom.Symptom?.Severity, lastSymptom.OccurredAt);

        return bp is null && symptom is null ? new HealthSummaryDiary(null, null) : new HealthSummaryDiary(bp, symptom);
    }

    private async Task<HealthSummaryRecords?> BuildRecordsAsync(Guid userId, MedicalRecordKind kind, CancellationToken ct)
    {
        var page = await records.GetVisibleRecordsAsync(
            userId, new MedicalRecordFilter(kind, MineOnly: true, PageSize: 1), ct);
        if (page.TotalCount == 0) return new HealthSummaryRecords(null, 0);

        var r = page.Items[0];
        int? prescriptionCount = null;
        if (kind == MedicalRecordKind.DoctorVisit)
        {
            var (result, conclusion) = await indicators.GetConclusionAsync(r.Id, userId, ct);
            if (result == ExtractionQueryResult.Success) prescriptionCount = conclusion!.PrescribedMedications.Count;
        }

        var latest = new HealthSummaryLatestRecord(
            r.Id, r.Title, r.RecordDate, r.Doctor, r.IndicatorCount, r.AbnormalIndicatorCount, prescriptionCount);
        return new HealthSummaryRecords(latest, page.TotalCount);
    }

    private async Task<HealthSummaryIndicators?> BuildIndicatorsAsync(Guid userId, CancellationToken ct)
    {
        var mine = await indicators.GetMyIndicatorsAsync(userId, ct);
        var self = mine.Where(i => i.FamilyDependentId is null && i.TargetUserId is null).ToList();
        if (self.Count == 0) return new HealthSummaryIndicators(0, null);

        var chosen = PickHighlight(self);
        var history = await indicators.GetHistoryAsync(userId, chosen.AnalyteKey, chosen.SpecimenKbId, null, null, ct);
        var recentNumeric = history
            .Select(h => decimal.TryParse(h.ValueNumericText, out var v) ? (decimal?)v : null)
            .Where(v => v is not null).Select(v => v!.Value)
            .TakeLast(SparklinePoints).ToList();

        var highlight = new HealthSummaryHighlightIndicator(
            chosen.AnalyteKey, chosen.DisplayName, chosen.ValueRaw, chosen.Unit, chosen.Flag,
            recentNumeric,
            history.Count > 0 ? history[0].RecordDate : chosen.LastRecordDate,
            chosen.LastRecordDate);

        return new HealthSummaryIndicators(self.Count, highlight);
    }

    /// <summary>Главный показатель плитки: сперва любой вне нормы (макет показывает именно такой
    /// пример), иначе — самый свежий. Настоящий выбор «по наибольшему изменению» потребовал бы истории
    /// КАЖДОГО показателя — намеренное упрощение для превью на плитке, полный список остаётся в
    /// разделе «Показатели». Выделено в чистую функцию — тестируется без похода в БД.</summary>
    public static MyIndicatorSummary PickHighlight(IReadOnlyList<MyIndicatorSummary> self) =>
        self.FirstOrDefault(i => i.Flag is not IndicatorFlag.Normal and not IndicatorFlag.Unknown)
            ?? self.OrderByDescending(i => i.LastRecordDate).First();

    private async Task<HealthSummaryVaccinations?> BuildVaccinationsAsync(Guid userId, CancellationToken ct)
    {
        var overview = await vaccinations.GetOverviewAsync(userId, ct);
        var due = overview.Attention
            .Where(a => a.Subject.IsSelf)
            .OrderBy(a => a.Item.WindowFrom)
            .Select(a => new HealthSummaryDueVaccine(a.Item.SeriesName, a.Item.Status, a.Item.WindowFrom, a.Item.WindowTo))
            .FirstOrDefault();

        var (result, schedule) = await vaccinations.GetPersonScheduleAsync(userId, VaccinationSubjects.UserKind, userId, ct);
        HealthSummaryLastVaccine? last = null;
        if (result == VaccinationResult.Success && schedule is not null)
        {
            var done = schedule.ByAge.Concat(schedule.Custom)
                .Where(i => i.Status is VaccinationStatus.Done or VaccinationStatus.HadDisease && i.Date is not null)
                .OrderByDescending(i => i.Date)
                .FirstOrDefault();
            if (done is not null) last = new HealthSummaryLastVaccine(done.Label, done.Date);
        }

        return due is null && last is null ? new HealthSummaryVaccinations(null, null) : new HealthSummaryVaccinations(due, last);
    }

    private async Task<HealthSummaryMedkits?> BuildMedkitsAsync(Guid userId, CancellationToken ct)
    {
        var familyIds = await access.GetActiveFamilyIdsAsync(userId, ct);
        if (familyIds.Count == 0) return new HealthSummaryMedkits(0, 0);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var expiringOrExpired = await db.Medications.AsNoTracking()
            .Where(m => familyIds.Contains(m.FamilyId))
            .WhereExpiringOrExpired(today, notificationOptions.Value.ExpiryWarningDays)
            .ToListAsync(ct);
        var expired = expiringOrExpired.Count(m => m.ExpiryDate!.Value < today);
        return new HealthSummaryMedkits(expiringOrExpired.Count - expired, expired);
    }

    private async Task<HealthSummaryReports?> BuildReportsAsync(Guid userId, CancellationToken ct)
    {
        var all = await reports.ListAsync(userId, ct);
        return new HealthSummaryReports(all.Count(r => r.Link.Status == DoctorReportLinkStatus.Active));
    }
}
