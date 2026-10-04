using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.HealthNotes;
using FamilyHub.Domain.ValueObjects;
using FamilyHub.Domain.Vaccinations;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.MedicalRecords;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.DoctorReports;

/// <summary>
/// Собирает данные пациента за период для отчёта врачу (<see cref="ReportSubject"/>):
/// <list type="bullet">
/// <item>сам автор — записи, где он и владелец, и адресат (без подопечных и без загруженных им за других),
/// плюс загруженные другими лично для него (TargetUserId); дневник целиком;</item>
/// <item>взрослый член семьи — те же записи этого человека, но только видимые автору (шаринг семье,
/// загруженные автором для него); дневник и прививки — только если человек дал автору грант;</item>
/// <item>подопечный — все его записи (их видит любой активный член семьи) и прививки; дневника у подопечных нет.</item>
/// </list>
/// Доступ к самому пациенту проверяет вызывающий (DoctorReportSubjects).
/// </summary>
public class DoctorReportDataCollector(AppDbContext db)
{
    private const int MaxLabDates = 6;
    private const int MaxLabRows = 60;
    private const int MaxNotes = 30;
    private const int MaxSeriesPoints = 60;

    /// <summary>Записи пациента за период (см. класс).</summary>
    private IQueryable<MedicalRecord> PatientRecords(ReportSubject subject, DateOnly from, DateOnly to)
    {
        IQueryable<MedicalRecord> query;
        if (subject.DependentId is { } dependentId)
        {
            query = db.MedicalRecords.AsNoTracking().Where(r => r.FamilyDependentId == dependentId);
        }
        else
        {
            var userId = subject.UserId!.Value;
            var source = subject.IsSelf ? db.MedicalRecords.AsNoTracking() : MedicalRecordVisibility.Visible(db, subject.ViewerId);
            query = source.Where(r =>
                (r.OwnerUserId == userId && r.FamilyDependentId == null && r.TargetUserId == null) || r.TargetUserId == userId);
        }
        return query.Where(r => r.RecordDate >= from && r.RecordDate <= to);
    }

    /// <summary>Чей дневник читать: null — дневник в отчёт не попадает (подопечный или нет гранта).</summary>
    private static Guid? DiaryOwner(ReportSubject subject) => subject.IncludeDiary ? subject.UserId : null;

    /// <summary>Границы дневника в UTC: [from 00:00, to+1 00:00). Часовой пояс пользователя серверу
    /// неизвестен — сутки считаются по UTC, расхождение не больше смещения пояса на краях периода.</summary>
    private static (DateTime FromUtc, DateTime ToUtc) DiaryBounds(DateOnly from, DateOnly to) =>
        (new DateTime(from.Year, from.Month, from.Day, 0, 0, 0, DateTimeKind.Utc),
         new DateTime(to.Year, to.Month, to.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(1));

    public Task<ReportCounts> CountAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        CountAsync(ReportSubject.Self(userId), from, to, ct);

    public async Task<ReportCounts> CountAsync(ReportSubject subject, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var records = PatientRecords(subject, from, to);
        var analyses = await records.CountAsync(r => r.Kind == MedicalRecordKind.Analysis, ct);
        var visits = await records.CountAsync(r => r.Kind == MedicalRecordKind.DoctorVisit, ct);
        if (DiaryOwner(subject) is not { } userId) return new ReportCounts(analyses, visits, 0, 0);

        var (fromUtc, toUtc) = DiaryBounds(from, to);
        var diary = await db.HealthNotes.AsNoTracking()
            .CountAsync(n => n.OwnerUserId == userId && n.OccurredAt >= fromUtc && n.OccurredAt < toUtc, ct);
        var flagged = await db.HealthNotes.AsNoTracking()
            .CountAsync(n => n.OwnerUserId == userId && n.OccurredAt >= fromUtc && n.OccurredAt < toUtc
                && n.Kind == HealthNoteKind.Note && n.IncludeInDoctorQuestions, ct);
        return new ReportCounts(analyses, visits, diary, flagged);
    }

    public Task<ReportModel> CollectAsync(
        Guid userId, DateOnly from, DateOnly to, ReportBlocks blocks, string? patientComment,
        CancellationToken ct = default) =>
        CollectAsync(ReportSubject.Self(userId), from, to, blocks, patientComment, ct);

    public async Task<ReportModel> CollectAsync(
        ReportSubject subject, DateOnly from, DateOnly to, ReportBlocks blocks, string? patientComment,
        CancellationToken ct = default)
    {
        var patient = await LoadPatientAsync(subject, ct);
        var records = await PatientRecords(subject, from, to).OrderBy(r => r.RecordDate).ToListAsync(ct);
        var analyses = records.Where(r => r.Kind == MedicalRecordKind.Analysis).ToList();
        var visitRecords = records.Where(r => r.Kind == MedicalRecordKind.DoctorVisit).ToList();

        var diary = DiaryOwner(subject) is { } diaryOwner ? await LoadDiaryAsync(diaryOwner, from, to, ct) : [];

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

        List<ReportVaccination>? vaccinations = blocks.Vaccinations
            ? (subject.IncludeVaccinations ? await BuildVaccinationsAsync(subject, from, to, ct) : [])
            : null;

        return new ReportModel(
            patient, from, to, DateTime.UtcNow, blocks,
            string.IsNullOrWhiteSpace(patientComment) ? null : patientComment.Trim(),
            flagged, labs, summaries, skipped, meds, visits, metrics, wellbeing, sleep, symptoms, notes, vaccinations);
    }

    /// <summary>Сделанные прививки и перенесённые болезни пациента за период. Название и доза —
    /// уже разрешены через VaccineCatalog по SeriesCode/DoseIndex, не сырые коды.</summary>
    private async Task<List<ReportVaccination>> BuildVaccinationsAsync(ReportSubject subject, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var bySubject = subject.DependentId is { } dependentId
            ? db.Vaccinations.AsNoTracking().Where(v => v.FamilyDependentId == dependentId)
            : db.Vaccinations.AsNoTracking().Where(v => v.SubjectUserId == subject.UserId);
        var rows = await bySubject
            .Where(v => (v.Kind == VaccinationKind.Done || v.Kind == VaccinationKind.HadDisease)
                        && v.Date != null && v.Date >= from && v.Date <= to)
            .OrderBy(v => v.Date)
            .ToListAsync(ct);

        return rows.Select(v =>
        {
            var series = VaccineCatalog.Find(v.SeriesCode);
            var name = series?.Name ?? v.CustomName ?? "Прививка не из календаря";
            var dose = v.DoseIndex is { } idx && series is not null
                ? (idx < series.Doses.Count ? series.Doses[idx].Label : "Ревакцинация")
                : "—";
            return new ReportVaccination(v.Date, name, dose, v.VaccineName, v.Kind == VaccinationKind.HadDisease);
        }).ToList();
    }

    private async Task<ReportPatient> LoadPatientAsync(ReportSubject subject, CancellationToken ct)
    {
        if (subject.DependentId is { } dependentId)
        {
            var dep = await db.FamilyDependents.AsNoTracking().SingleAsync(d => d.Id == dependentId, ct);
            if (dep.IsPet)
            {
                var petName = string.IsNullOrWhiteSpace(dep.PetSpecies) ? dep.FirstName.Trim() : $"{dep.FirstName.Trim()} ({dep.PetSpecies.Trim()})";
                var petSex = dep.Gender switch { Gender.Male => "самец", Gender.Female => "самка", _ => null };
                return new ReportPatient(petName, dep.FirstName.Trim(), petSex, dep.BirthDate, IsPet: true);
            }
            return BuildPatient(dep.LastName, dep.FirstName, dep.MiddleName, dep.Gender, dep.BirthDate);
        }

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == subject.UserId, ct);
        return BuildPatient(user.LastName, user.FirstName, user.MiddleName, user.Gender, user.BirthDate);
    }

    private static ReportPatient BuildPatient(string? lastName, string? firstName, string? middleName, Gender? gender, DateOnly? birthDate)
    {
        var full = PersonName.Format(lastName, firstName, middleName, PersonNameStyle.Full).Trim();
        var shortName = PersonName.Format(lastName, firstName, middleName, PersonNameStyle.Initials).Trim();
        var sex = gender switch { Gender.Male => "м", Gender.Female => "ж", _ => null };
        return new ReportPatient(string.IsNullOrEmpty(full) ? "Пациент" : full, string.IsNullOrEmpty(shortName) ? "Пациент" : shortName, sex, birthDate);
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
                hasDeviation,
                ordered.LastOrDefault(i => i.PanelLabel is not null)?.PanelLabel));
        }

        // Отбор в лимит — как и раньше, отклонения в приоритете; разделы бланка меняют только порядок
        // показа уже отобранных строк.
        var shown = rows
            .OrderByDescending(r => r.HasDeviation)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxLabRows)
            .ToList();
        return new ReportLabTable(dates, OrderByPanel(shown), total - shown.Count);
    }

    /// <summary>Строки одного раздела бланка — подряд (рендер ставит над ними подзаголовок): разделы с
    /// отклонениями выше, затем по алфавиту, строки без раздела — в конце; внутри раздела порядок
    /// прежний (отклонения сверху, затем по алфавиту). Без единого раздела — порядок не меняется.</summary>
    private static List<ReportLabRow> OrderByPanel(List<ReportLabRow> rows)
    {
        if (!rows.Any(r => r.Panel is not null)) return rows;
        return rows
            .GroupBy(r => r.Panel)
            .OrderBy(g => g.Key is null)
            .ThenByDescending(g => g.Any(r => r.HasDeviation))
            .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .SelectMany(g => g)
            .ToList();
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

    /// <summary>Врачу — клиническая сводка (ClinicianLabSummarizer, план "качество
    /// ИИ-распознавания анализов", Этап 4), не пациентская: плотнее, клиническим языком, без
    /// разжёвывания. Fallback на пациентскую (LabSummarizer.SummaryJson) — шаг клинической сводки
    /// может быть выключен из админки, не пройти антигаллюцинационный гейт, или запись создана до
    /// введения ClinicianLabSummarizer — отчёт врачу в этих случаях лучше отдаст то, что есть, чем
    /// пропустит запись вовсе. ReportSummaryItem/LabSummaryDeviation не меняются — клиническое
    /// отклонение (Name, Clinical) проецируется в ту же форму (Name, Meaning), рендерер ничего не
    /// знает про источник текста.</summary>
    private static (List<ReportSummaryItem> Items, int Skipped) BuildSummaries(List<MedicalRecord> analyses)
    {
        var items = new List<ReportSummaryItem>();
        var skipped = 0;
        foreach (var record in analyses.Where(
            r => !string.IsNullOrEmpty(r.SummaryJson) || !string.IsNullOrEmpty(r.ClinicianSummaryJson)))
        {
            // Резюме устарело (показатели правили руками, пересчёт не завершён) — не отдаём врачу
            // устаревший текст; считаем, чтобы отчёт честно сказал, что часть резюме пропущена.
            if (record.SummaryDirtyAt is not null)
            {
                skipped++;
                continue;
            }

            var clinician = TryDeserialize<ClinicianLabSummary>(record.ClinicianSummaryJson);
            if (clinician is not null && (!string.IsNullOrWhiteSpace(clinician.Overview) || clinician.Deviations.Count > 0))
            {
                var deviations = clinician.Deviations.Select(d => new LabSummaryDeviation(d.Name, d.Clinical)).ToList();
                items.Add(new ReportSummaryItem(record.RecordDate, record.Title, clinician.Overview, deviations));
                continue;
            }

            var summary = TryDeserialize<LabSummary>(record.SummaryJson);
            if (summary is null || (string.IsNullOrWhiteSpace(summary.PlainSummary) && summary.Deviations.Count == 0)) continue;
            items.Add(new ReportSummaryItem(record.RecordDate, record.Title, summary.PlainSummary, summary.Deviations));
        }
        return (items, skipped);
    }

    private static T? TryDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
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
