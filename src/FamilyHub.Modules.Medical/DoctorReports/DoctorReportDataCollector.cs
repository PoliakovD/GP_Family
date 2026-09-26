using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.HealthNotes;
using FamilyHub.Domain.ValueObjects;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Extraction;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.DoctorReports;

/// <summary>
/// Собирает данные пациента за период для отчёта врачу. «Пациент» здесь — только сам пользователь:
/// записи, где он и владелец, и адресат (без подопечных и без загруженных им за других), плюс записи,
/// которые другие загрузили лично для него (TargetUserId). Шаринг семье на отчёт не влияет —
/// отчёт строит владелец про себя. Дневник — строго личный, берётся целиком по OwnerUserId.
/// </summary>
public class DoctorReportDataCollector(AppDbContext db)
{
    private const int MaxLabDates = 6;
    private const int MaxLabRows = 60;
    private const int MaxNotes = 30;
    private const int MaxSeriesPoints = 60;

    /// <summary>Записи, где пациент — сам пользователь (см. класс).</summary>
    private IQueryable<MedicalRecord> PatientRecords(Guid userId, DateOnly from, DateOnly to) =>
        db.MedicalRecords.AsNoTracking().Where(r =>
            r.RecordDate >= from && r.RecordDate <= to
            && ((r.OwnerUserId == userId && r.FamilyDependentId == null && r.TargetUserId == null)
                || r.TargetUserId == userId));

    /// <summary>Границы дневника в UTC: [from 00:00, to+1 00:00). Часовой пояс пользователя серверу
    /// неизвестен — сутки считаются по UTC, расхождение не больше смещения пояса на краях периода.</summary>
    private static (DateTime FromUtc, DateTime ToUtc) DiaryBounds(DateOnly from, DateOnly to) =>
        (new DateTime(from.Year, from.Month, from.Day, 0, 0, 0, DateTimeKind.Utc),
         new DateTime(to.Year, to.Month, to.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(1));

    public async Task<ReportCounts> CountAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var records = PatientRecords(userId, from, to);
        var analyses = await records.CountAsync(r => r.Kind == MedicalRecordKind.Analysis, ct);
        var visits = await records.CountAsync(r => r.Kind == MedicalRecordKind.DoctorVisit, ct);
        var (fromUtc, toUtc) = DiaryBounds(from, to);
        var diary = await db.HealthNotes.AsNoTracking()
            .CountAsync(n => n.OwnerUserId == userId && n.OccurredAt >= fromUtc && n.OccurredAt < toUtc, ct);
        var flagged = await db.HealthNotes.AsNoTracking()
            .CountAsync(n => n.OwnerUserId == userId && n.OccurredAt >= fromUtc && n.OccurredAt < toUtc
                && n.Kind == HealthNoteKind.Note && n.IncludeInDoctorQuestions, ct);
        return new ReportCounts(analyses, visits, diary, flagged);
    }

    public async Task<ReportModel> CollectAsync(
        Guid userId, DateOnly from, DateOnly to, ReportBlocks blocks, string? patientComment,
        CancellationToken ct = default)
    {
        var patient = await LoadPatientAsync(userId, ct);
        var records = await PatientRecords(userId, from, to).OrderBy(r => r.RecordDate).ToListAsync(ct);
        var analyses = records.Where(r => r.Kind == MedicalRecordKind.Analysis).ToList();
        var visitRecords = records.Where(r => r.Kind == MedicalRecordKind.DoctorVisit).ToList();

        var diary = await LoadDiaryAsync(userId, from, to, ct);

        var flagged = diary
            .Where(d => d.Note.Kind == HealthNoteKind.Note && d.Note.IncludeInDoctorQuestions && !string.IsNullOrWhiteSpace(d.Content.Text))
            .Select(d => new ReportNote(d.Note.OccurredAt, d.Content.Text!))
            .ToList();

        ReportLabTable? labs = null;
        if (blocks.Labs) labs = await BuildLabTableAsync(analyses, ct);

        List<ReportSummaryItem>? summaries = null;
        var skipped = 0;
        if (blocks.AiSummaries) (summaries, skipped) = BuildSummaries(analyses);

        List<ReportVisit>? visits = blocks.Visits ? BuildVisits(visitRecords) : null;
        List<ReportMedication>? meds = blocks.Medications ? BuildMedications(visitRecords, diary) : null;

        List<ReportMetric>? metrics = null;
        ReportWellbeing? wellbeing = null;
        ReportSleep? sleep = null;
        if (blocks.Measurements)
        {
            metrics = BuildMetrics(diary);
            wellbeing = BuildWellbeing(diary);
            sleep = BuildSleep(diary);
        }

        List<ReportSymptom>? symptoms = null;
        List<ReportNote>? notes = null;
        if (blocks.SymptomsNotes)
        {
            symptoms = BuildSymptoms(diary);
            // Заметки, помеченные «в вопросы к врачу», уже стоят в блоке жалоб — здесь не дублируем.
            notes = diary
                .Where(d => d.Note.Kind == HealthNoteKind.Note && !d.Note.IncludeInDoctorQuestions && !string.IsNullOrWhiteSpace(d.Content.Text))
                .OrderByDescending(d => d.Note.OccurredAt)
                .Take(MaxNotes)
                .OrderBy(d => d.Note.OccurredAt)
                .Select(d => new ReportNote(d.Note.OccurredAt, d.Content.Text!))
                .ToList();
        }

        return new ReportModel(
            patient, from, to, DateTime.UtcNow, blocks,
            string.IsNullOrWhiteSpace(patientComment) ? null : patientComment.Trim(),
            flagged, labs, summaries, skipped, meds, visits, metrics, wellbeing, sleep, symptoms, notes);
    }

    private async Task<ReportPatient> LoadPatientAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
        var full = PersonName.Format(user.LastName, user.FirstName, user.MiddleName, PersonNameStyle.Full).Trim();
        var shortName = PersonName.Format(user.LastName, user.FirstName, user.MiddleName, PersonNameStyle.Initials).Trim();
        var sex = user.Gender switch { Gender.Male => "м", Gender.Female => "ж", _ => null };
        return new ReportPatient(string.IsNullOrEmpty(full) ? "Пациент" : full, string.IsNullOrEmpty(shortName) ? "Пациент" : shortName, sex, user.BirthDate);
    }

    private sealed record DiaryEntry(HealthNote Note, HealthNoteContent Content);

    private async Task<List<DiaryEntry>> LoadDiaryAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (fromUtc, toUtc) = DiaryBounds(from, to);
        var rows = await db.HealthNotes.AsNoTracking()
            .Where(n => n.OwnerUserId == userId && n.OccurredAt >= fromUtc && n.OccurredAt < toUtc)
            .OrderBy(n => n.OccurredAt)
            .ToListAsync(ct);
        return rows.Select(n => new DiaryEntry(n, HealthNoteRules.ParseContent(n.Kind, n.Title, n.Text, n.DataJson))).ToList();
    }

    // ---- Анализы ----

    private async Task<ReportLabTable> BuildLabTableAsync(List<MedicalRecord> analyses, CancellationToken ct)
    {
        var ids = analyses.Select(a => a.Id).ToList();
        if (ids.Count == 0) return new ReportLabTable([], [], 0);

        var indicators = await db.LabIndicators.AsNoTracking()
            .Where(i => ids.Contains(i.MedicalRecordId))
            .ToListAsync(ct);

        // Колонки — самые свежие даты (не больше MaxLabDates), слева направо от старых к новым.
        var dates = indicators.Select(i => i.RecordDate).Distinct().OrderByDescending(d => d).Take(MaxLabDates).OrderBy(d => d).ToList();
        var dateSet = dates.ToHashSet();

        var rows = new List<ReportLabRow>();
        var total = 0;
        foreach (var group in indicators.Where(i => dateSet.Contains(i.RecordDate)).GroupBy(i => (i.AnalyteKey, i.SpecimenKbId)))
        {
            total++;
            var ordered = group.OrderBy(i => i.RecordDate).ThenBy(i => i.CreatedAt).ToList();
            // Два значения в один день — берём последнее.
            var cells = ordered
                .GroupBy(i => i.RecordDate)
                .Select(g => g.Last())
                .Select(i => new ReportLabCell(i.RecordDate, i.ValueRaw, i.Flag))
                .ToList();
            var latest = ordered[^1];
            var hasDeviation = cells.Any(c => c.Flag is IndicatorFlag.Low or IndicatorFlag.High or IndicatorFlag.Critical);

            // Динамика — показатель, который мерили не раз или у которого есть отклонение; единичные
            // «в норме» шумят и уходят в счётчик «ещё N показателей».
            if (!hasDeviation && cells.Count < 2) continue;

            rows.Add(new ReportLabRow(
                latest.DisplayName,
                ordered.Select(i => i.Unit).LastOrDefault(u => !string.IsNullOrWhiteSpace(u)),
                Reference(latest),
                cells,
                hasDeviation));
        }

        var shown = rows
            .OrderByDescending(r => r.HasDeviation)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxLabRows)
            .ToList();
        return new ReportLabTable(dates, shown, total - shown.Count);
    }

    private static string? Reference(LabIndicator i)
    {
        if (!string.IsNullOrWhiteSpace(i.RefText)) return i.RefText;
        var lo = i.RefLowText;
        var hi = i.RefHighText;
        if (!string.IsNullOrWhiteSpace(lo) && !string.IsNullOrWhiteSpace(hi)) return $"{lo}–{hi}";
        if (!string.IsNullOrWhiteSpace(lo)) return $"≥ {lo}";
        if (!string.IsNullOrWhiteSpace(hi)) return $"≤ {hi}";
        return null;
    }

    private static (List<ReportSummaryItem> Items, int Skipped) BuildSummaries(List<MedicalRecord> analyses)
    {
        var items = new List<ReportSummaryItem>();
        var skipped = 0;
        foreach (var record in analyses.Where(r => !string.IsNullOrEmpty(r.SummaryJson)))
        {
            // Резюме устарело (показатели правили руками, пересчёт не завершён) — не отдаём врачу
            // устаревший текст; считаем, чтобы отчёт честно сказал, что часть резюме пропущена.
            if (record.SummaryDirtyAt is not null)
            {
                skipped++;
                continue;
            }

            LabSummary? summary;
            try
            {
                summary = JsonSerializer.Deserialize<LabSummary>(record.SummaryJson!);
            }
            catch (JsonException)
            {
                continue;
            }
            if (summary is null || (string.IsNullOrWhiteSpace(summary.PlainSummary) && summary.Deviations.Count == 0)) continue;
            items.Add(new ReportSummaryItem(record.RecordDate, record.Title, summary.PlainSummary, summary.Deviations));
        }
        return (items, skipped);
    }

    // ---- Визиты и препараты ----

    private static VisitConclusion? ParseConclusion(MedicalRecord r)
    {
        if (string.IsNullOrEmpty(r.ExtractedDataJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<VisitConclusion>(r.ExtractedDataJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<ReportVisit> BuildVisits(List<MedicalRecord> visitRecords) =>
        visitRecords.Select(r =>
        {
            var c = ParseConclusion(r);
            return new ReportVisit(r.RecordDate, r.Doctor, r.Title, r.Description, c?.Diagnosis, c?.Recommendations);
        }).ToList();

    private static List<ReportMedication> BuildMedications(List<MedicalRecord> visitRecords, List<DiaryEntry> diary)
    {
        var byName = new Dictionary<string, ReportMedication>(StringComparer.CurrentCultureIgnoreCase);

        // Назначения из визитов: поздний визит перекрывает ранний по тому же названию.
        foreach (var visit in visitRecords.OrderBy(v => v.RecordDate))
        {
            foreach (var med in ParseConclusion(visit)?.PrescribedMedications ?? [])
            {
                var name = med.Name?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                byName[name] = new ReportMedication(name, med.DosageInstructions, visit.RecordDate, visit.Doctor, 0, null);
            }
        }

        // Приёмы из дневника: дополняют назначение (сколько раз принят) либо идут отдельной строкой.
        foreach (var group in diary.Where(d => d.Note.Kind == HealthNoteKind.MedicationIntake && !string.IsNullOrWhiteSpace(d.Content.Title))
                     .GroupBy(d => d.Content.Title!.Trim(), StringComparer.CurrentCultureIgnoreCase))
        {
            var last = group.OrderBy(d => d.Note.OccurredAt).Last();
            var count = group.Count();
            if (byName.TryGetValue(group.Key, out var prescribed))
            {
                byName[group.Key] = prescribed with { IntakeCount = count, LastIntake = last.Note.OccurredAt };
            }
            else
            {
                byName[group.Key] = new ReportMedication(group.Key, last.Content.Intake?.Dose, null, null, count, last.Note.OccurredAt);
            }
        }

        return byName.Values.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ---- Дневник ----

    private static List<ReportMetric> BuildMetrics(List<DiaryEntry> diary)
    {
        var result = new List<ReportMetric>();
        foreach (var def in HealthMetricCatalog.All)
        {
            var points = diary
                .Where(d => d.Note.Kind == HealthNoteKind.Metric && d.Content.Metric?.Code == def.Code)
                .Select(d => (At: d.Note.OccurredAt, M: d.Content.Metric!))
                .OrderBy(p => p.At)
                .ToList();
            if (points.Count == 0) continue;

            var values = points.Select(p => p.M.Value).ToList();
            var second = points.Where(p => p.M.Value2 is not null).Select(p => p.M.Value2!.Value).ToList();
            var last = points[^1];
            result.Add(new ReportMetric(
                def.Code, def.Name, def.Unit, points.Count,
                values.Min(), values.Max(), Math.Round(values.Average(), 1), last.M.Value, last.At,
                second.Count > 0 ? second.Min() : null,
                second.Count > 0 ? second.Max() : null,
                second.Count > 0 ? Math.Round(second.Average(), 1) : null,
                last.M.Value2,
                values.Skip(Math.Max(0, values.Count - MaxSeriesPoints)).ToList()));
        }
        return result;
    }

    private static ReportWellbeing? BuildWellbeing(List<DiaryEntry> diary)
    {
        var scores = diary.Where(d => d.Note.Kind == HealthNoteKind.Wellbeing && d.Content.Wellbeing is not null)
            .Select(d => d.Content.Wellbeing!.Score).ToList();
        return scores.Count == 0 ? null : new ReportWellbeing(scores.Count, Math.Round(scores.Average(), 1), scores.Min());
    }

    private static ReportSleep? BuildSleep(List<DiaryEntry> diary)
    {
        var sleeps = diary.Where(d => d.Note.Kind == HealthNoteKind.Sleep && d.Content.Sleep is not null)
            .Select(d => d.Content.Sleep!).ToList();
        return sleeps.Count == 0
            ? null
            : new ReportSleep(
                sleeps.Count,
                (int)Math.Round(sleeps.Average(HealthNoteRules.SleepMinutes)),
                Math.Round(sleeps.Average(s => s.Quality), 1));
    }

    private static List<ReportSymptom> BuildSymptoms(List<DiaryEntry> diary) =>
        diary.Where(d => d.Note.Kind == HealthNoteKind.Symptom && d.Content.Symptom is not null && !string.IsNullOrWhiteSpace(d.Content.Title))
            .GroupBy(d => d.Content.Title!.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .Select(g =>
            {
                var severities = g.Select(d => d.Content.Symptom!.Severity).ToList();
                var areas = g.SelectMany(d => d.Content.Symptom!.Areas ?? [])
                    .GroupBy(a => a).OrderByDescending(a => a.Count()).Take(3).Select(a => a.Key).ToList();
                return new ReportSymptom(
                    g.Key, g.Count(), Math.Round(severities.Average(), 1), severities.Max(),
                    g.Max(d => d.Note.OccurredAt), areas);
            })
            .OrderByDescending(s => s.Episodes)
            .ThenBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
