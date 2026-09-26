using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.HealthNotes;
using FamilyHub.Modules.Medical.DoctorReports;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.TestUtils;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical;

public class DoctorReportDataCollectorTests : SqliteTestBase
{
    private static readonly DateOnly From = new(2026, 3, 1);
    private static readonly DateOnly To = new(2026, 9, 30);
    private static readonly ReportBlocks All = new(true, true, true, true, true, true);

    private readonly DoctorReportDataCollector _sut;
    private readonly User _me;

    public DoctorReportDataCollectorTests()
    {
        _sut = new DoctorReportDataCollector(Db);
        _me = Db.AddUser();
        _me.LastName = "Поляков";
        _me.FirstName = "Даниил";
        _me.MiddleName = "Александрович";
        _me.BirthDate = new DateOnly(1998, 5, 4);
        _me.Gender = Gender.Male;
        Db.SaveChanges();
    }

    private MedicalRecord AddRecord(Guid owner, DateOnly date, MedicalRecordKind kind = MedicalRecordKind.Analysis,
        Guid? target = null, string? title = null, string? summaryJson = null, DateTime? dirtyAt = null, string? extracted = null, string? doctor = null)
    {
        var r = TestData.NewMedicalRecord(owner, kind);
        r.RecordDate = date;
        r.TargetUserId = target;
        r.Title = title;
        r.SummaryJson = summaryJson;
        r.SummaryDirtyAt = dirtyAt;
        r.ExtractedDataJson = extracted;
        r.Doctor = doctor;
        Db.MedicalRecords.Add(r);
        Db.SaveChanges();
        return r;
    }

    private void AddIndicator(MedicalRecord record, string key, string value, IndicatorFlag flag = IndicatorFlag.Normal,
        string? unit = null, string? refLow = null, string? refHigh = null)
    {
        Db.LabIndicators.Add(new LabIndicator
        {
            Id = Guid.NewGuid(),
            MedicalRecordId = record.Id,
            RecordDate = record.RecordDate,
            OwnerUserId = record.OwnerUserId,
            TargetUserId = record.TargetUserId,
            AnalyteKey = key,
            DisplayName = key,
            Flag = flag,
            ValueRaw = value,
            Unit = unit,
            RefLowText = refLow,
            RefHighText = refHigh,
            CreatedAt = DateTime.UtcNow,
        });
        Db.SaveChanges();
    }

    private HealthNote AddNote(HealthNoteContent content, DateTime at, bool flagged = false)
    {
        var n = new HealthNote
        {
            Id = Guid.NewGuid(),
            OwnerUserId = _me.Id,
            Kind = content.Kind,
            OccurredAt = at,
            Title = content.Title,
            Text = content.Text,
            DataJson = HealthNoteRules.SerializePayload(content),
            IncludeInDoctorQuestions = flagged,
            CreatedAt = at,
            UpdatedAt = at,
        };
        Db.HealthNotes.Add(n);
        Db.SaveChanges();
        return n;
    }

    private static DateTime Utc(int m, int d, int h = 9) => new(2026, m, d, h, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Patient_IsSelf_WithNameSexAndBirthDate()
    {
        var model = await _sut.CollectAsync(_me.Id, From, To, All, null);

        model.Patient.FullName.Should().Be("Поляков Даниил Александрович");
        model.Patient.ShortName.Should().Be("Поляков Д.А.");
        model.Patient.Sex.Should().Be("м");
        model.Patient.BirthDate.Should().Be(new DateOnly(1998, 5, 4));
    }

    [Fact]
    public async Task Records_OnlyThoseWherePatientIsMe_AreIncluded()
    {
        var other = Db.AddUser();
        var mine = AddRecord(_me.Id, new DateOnly(2026, 5, 1), title: "Мой ОАК");
        var forMe = AddRecord(other.Id, new DateOnly(2026, 6, 1), target: _me.Id, title: "Загружено для меня");
        var uploadedForOther = AddRecord(_me.Id, new DateOnly(2026, 7, 1), target: other.Id, title: "Я загрузил для другого");
        var strangers = AddRecord(other.Id, new DateOnly(2026, 8, 1), title: "Чужой");
        foreach (var r in new[] { mine, forMe, uploadedForOther, strangers }) AddIndicator(r, $"показатель-{r.Title}", "1", IndicatorFlag.High);

        var model = await _sut.CollectAsync(_me.Id, From, To, All, null);

        model.Labs!.Rows.Select(r => r.Name).Should().BeEquivalentTo(["показатель-Мой ОАК", "показатель-Загружено для меня"]);
    }

    [Fact]
    public async Task Records_OutsideThePeriod_AreExcluded()
    {
        var early = AddRecord(_me.Id, new DateOnly(2026, 1, 10));
        AddIndicator(early, "ферритин", "9", IndicatorFlag.Low);

        var model = await _sut.CollectAsync(_me.Id, From, To, All, null);

        model.Labs!.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Labs_DeviationsFirst_SingleNormalValuesOmitted_DynamicsKept()
    {
        var r1 = AddRecord(_me.Id, new DateOnly(2026, 5, 10));
        var r2 = AddRecord(_me.Id, new DateOnly(2026, 8, 10));
        AddIndicator(r1, "гемоглобин", "118", IndicatorFlag.Low, "г/л", "130", "160");
        AddIndicator(r2, "гемоглобин", "134", IndicatorFlag.Normal, "г/л", "130", "160");
        AddIndicator(r1, "креатинин", "80", IndicatorFlag.Normal);
        AddIndicator(r2, "креатинин", "82", IndicatorFlag.Normal);
        AddIndicator(r2, "ферритин", "9", IndicatorFlag.Low);
        AddIndicator(r2, "глюкоза", "5", IndicatorFlag.Normal); // единичный «в норме» — шум

        var labs = (await _sut.CollectAsync(_me.Id, From, To, All, null)).Labs!;

        labs.Dates.Should().Equal(new DateOnly(2026, 5, 10), new DateOnly(2026, 8, 10));
        labs.Rows.Select(r => r.Name).Should().Equal("гемоглобин", "ферритин", "креатинин");
        labs.Rows[0].HasDeviation.Should().BeTrue();
        labs.Rows[0].Reference.Should().Be("130–160");
        labs.Rows[0].Unit.Should().Be("г/л");
        labs.Rows[0].Cells.Select(c => c.Text).Should().Equal("118", "134");
        labs.OmittedCount.Should().Be(1);
    }

    [Fact]
    public async Task Labs_KeepsOnlyTheSixLatestDates()
    {
        for (var month = 1; month <= 8; month++)
        {
            var r = AddRecord(_me.Id, new DateOnly(2026, month, 5));
            AddIndicator(r, "гемоглобин", (120 + month).ToString(), IndicatorFlag.Normal);
        }

        var labs = (await _sut.CollectAsync(_me.Id, new DateOnly(2026, 1, 1), To, All, null)).Labs!;

        labs.Dates.Should().HaveCount(6);
        labs.Dates.First().Should().Be(new DateOnly(2026, 3, 5));
        labs.Dates.Last().Should().Be(new DateOnly(2026, 8, 5));
    }

    [Fact]
    public async Task Summaries_SkipStaleOnes_AndCountThem()
    {
        var ok = JsonSerializer.Serialize(new LabSummary("Всё хорошо", [new LabSummaryDeviation("Ферритин", "снижен")], [], "Не диагноз"));
        AddRecord(_me.Id, new DateOnly(2026, 5, 1), title: "Свежее", summaryJson: ok);
        AddRecord(_me.Id, new DateOnly(2026, 6, 1), title: "Устаревшее", summaryJson: ok, dirtyAt: DateTime.UtcNow);
        AddRecord(_me.Id, new DateOnly(2026, 7, 1), title: "Битое", summaryJson: "{not json");

        var model = await _sut.CollectAsync(_me.Id, From, To, All, null);

        model.Summaries!.Should().ContainSingle(s => s.Title == "Свежее" && s.PlainSummary == "Всё хорошо");
        model.Summaries![0].Deviations.Should().ContainSingle(d => d.Name == "Ферритин");
        model.SkippedSummaries.Should().Be(1);
    }

    [Fact]
    public async Task Visits_ContainConclusion_AndMedicationsMergePrescriptionsWithDiaryIntakes()
    {
        var conclusion = JsonSerializer.Serialize(new VisitConclusion(
            "ЖДА", "Повторить ферритин", null, null,
            [new PrescribedMedication("Сорбифер Дурулес", "1 таб. 2 раза в день"), new PrescribedMedication("Аскорбиновая кислота", null)]));
        AddRecord(_me.Id, new DateOnly(2026, 8, 12), MedicalRecordKind.DoctorVisit, title: "Терапевт", extracted: conclusion, doctor: "Смирнова");
        AddNote(new HealthNoteContent(HealthNoteKind.MedicationIntake, "сорбифер дурулес", null, Intake: new MedicationIntakeData("1 таблетка")), Utc(9, 1));
        AddNote(new HealthNoteContent(HealthNoteKind.MedicationIntake, "сорбифер дурулес", null, Intake: new MedicationIntakeData("1 таблетка")), Utc(9, 2));
        AddNote(new HealthNoteContent(HealthNoteKind.MedicationIntake, "Нурофен", null, Intake: new MedicationIntakeData("200 мг")), Utc(9, 3));

        var model = await _sut.CollectAsync(_me.Id, From, To, All, null);

        model.Visits!.Should().ContainSingle(v => v.Doctor == "Смирнова" && v.Diagnosis == "ЖДА" && v.Recommendations == "Повторить ферритин");
        var meds = model.Medications!;
        meds.Select(m => m.Name).Should().Equal("Аскорбиновая кислота", "Нурофен", "Сорбифер Дурулес");

        var sorbifer = meds.Single(m => m.Name == "Сорбифер Дурулес");
        sorbifer.Dosage.Should().Be("1 таб. 2 раза в день", "схема — из назначения врача, не из дневника");
        sorbifer.Prescriber.Should().Be("Смирнова");
        sorbifer.IntakeCount.Should().Be(2, "приёмы из дневника сливаются с назначением по названию без учёта регистра");

        var nurofen = meds.Single(m => m.Name == "Нурофен");
        nurofen.Prescriber.Should().BeNull();
        nurofen.Dosage.Should().Be("200 мг");
        nurofen.IntakeCount.Should().Be(1);
    }

    [Fact]
    public async Task Measurements_AggregatePerMetric_WithPairedValuesAndSeries()
    {
        foreach (var (day, sys, dia) in new[] { (1, 120m, 80m), (2, 130m, 86m), (3, 125m, 82m) })
            AddNote(new HealthNoteContent(HealthNoteKind.Metric, null, null, Metric: new MetricData("blood_pressure", sys, dia)), Utc(9, day));
        AddNote(new HealthNoteContent(HealthNoteKind.Metric, null, null, Metric: new MetricData("pulse", 72, null)), Utc(9, 3));

        var metrics = (await _sut.CollectAsync(_me.Id, From, To, All, null)).Metrics!;

        metrics.Select(m => m.Code).Should().Equal("blood_pressure", "pulse");
        var bp = metrics[0];
        (bp.Count, bp.Min, bp.Max, bp.Avg, bp.Last).Should().Be((3, 120m, 130m, 125m, 125m));
        (bp.Min2, bp.Max2, bp.Last2).Should().Be((80m, 86m, 82m));
        bp.Series.Should().Equal(120m, 130m, 125m);
        metrics[1].Min2.Should().BeNull();
    }

    [Fact]
    public async Task WellbeingAndSleep_AreSummarised()
    {
        AddNote(new HealthNoteContent(HealthNoteKind.Wellbeing, null, null, Wellbeing: new WellbeingData(4, null)), Utc(9, 1));
        AddNote(new HealthNoteContent(HealthNoteKind.Wellbeing, null, null, Wellbeing: new WellbeingData(2, null)), Utc(9, 2));
        AddNote(new HealthNoteContent(HealthNoteKind.Sleep, null, null,
            Sleep: new SleepData(Utc(9, 1, 0), Utc(9, 1, 0).AddMinutes(420), 3)), Utc(9, 1));
        AddNote(new HealthNoteContent(HealthNoteKind.Sleep, null, null,
            Sleep: new SleepData(Utc(9, 2, 0), Utc(9, 2, 0).AddMinutes(480), 2)), Utc(9, 2));

        var model = await _sut.CollectAsync(_me.Id, From, To, All, null);

        (model.Wellbeing!.Count, model.Wellbeing.Average, model.Wellbeing.Worst).Should().Be((2, 3.0, 2));
        (model.Sleep!.Count, model.Sleep.AverageMinutes, model.Sleep.AverageQuality).Should().Be((2, 450, 2.5));
    }

    [Fact]
    public async Task Symptoms_GroupedByTitle_CaseInsensitively_AndFlaggedNotesGoToComplaints()
    {
        AddNote(new HealthNoteContent(HealthNoteKind.Symptom, "Головная боль", null, Symptom: new SymptomData(5, ["head"], null)), Utc(9, 1));
        AddNote(new HealthNoteContent(HealthNoteKind.Symptom, "головная боль", null, Symptom: new SymptomData(7, ["head", "throat"], null)), Utc(9, 5));
        AddNote(new HealthNoteContent(HealthNoteKind.Note, null, "Спросить про железо"), Utc(9, 6), flagged: true);
        AddNote(new HealthNoteContent(HealthNoteKind.Note, null, "Обычная заметка"), Utc(9, 7));

        var model = await _sut.CollectAsync(_me.Id, From, To, All, "Слабость");

        var symptom = model.Symptoms!.Single();
        (symptom.Episodes, symptom.AverageSeverity, symptom.MaxSeverity).Should().Be((2, 6.0, 7));
        symptom.Areas.Should().StartWith("head");
        model.FlaggedNotes.Should().ContainSingle(n => n.Text == "Спросить про железо");
        model.Notes!.Should().ContainSingle(n => n.Text == "Обычная заметка", "помеченная заметка уже в жалобах — не дублируется");
        model.PatientComment.Should().Be("Слабость");
    }

    [Fact]
    public async Task DisabledBlocks_AreNull()
    {
        AddNote(new HealthNoteContent(HealthNoteKind.Metric, null, null, Metric: new MetricData("pulse", 70, null)), Utc(9, 1));
        var record = AddRecord(_me.Id, new DateOnly(2026, 5, 1));
        AddIndicator(record, "ферритин", "9", IndicatorFlag.Low);

        var model = await _sut.CollectAsync(_me.Id, From, To, new ReportBlocks(false, false, false, false, false, false), null);

        model.Labs.Should().BeNull();
        model.Summaries.Should().BeNull();
        model.Medications.Should().BeNull();
        model.Visits.Should().BeNull();
        model.Metrics.Should().BeNull();
        model.Symptoms.Should().BeNull();
        model.HasContent.Should().BeFalse();
    }

    [Fact]
    public async Task Diary_OfOtherUsers_IsNeverIncluded()
    {
        var other = Db.AddUser();
        Db.HealthNotes.Add(new HealthNote
        {
            Id = Guid.NewGuid(), OwnerUserId = other.Id, Kind = HealthNoteKind.Metric, OccurredAt = Utc(9, 1),
            DataJson = HealthNoteRules.SerializePayload(new HealthNoteContent(HealthNoteKind.Metric, null, null, Metric: new MetricData("pulse", 99, null))),
            CreatedAt = Utc(9, 1), UpdatedAt = Utc(9, 1),
        });
        Db.SaveChanges();

        var model = await _sut.CollectAsync(_me.Id, From, To, All, null);

        model.Metrics.Should().BeEmpty();
    }

    [Fact]
    public async Task Count_ReportsAnalysesVisitsAndDiaryEntriesInPeriod()
    {
        AddRecord(_me.Id, new DateOnly(2026, 5, 1));
        AddRecord(_me.Id, new DateOnly(2026, 5, 2));
        AddRecord(_me.Id, new DateOnly(2026, 6, 1), MedicalRecordKind.DoctorVisit);
        AddRecord(_me.Id, new DateOnly(2025, 1, 1)); // вне периода
        AddNote(new HealthNoteContent(HealthNoteKind.Note, null, "а"), Utc(9, 1));
        AddNote(new HealthNoteContent(HealthNoteKind.Note, null, "б"), Utc(9, 2), flagged: true);

        var counts = await _sut.CountAsync(_me.Id, From, To);

        counts.Should().Be(new ReportCounts(2, 1, 2, 1));
    }
}
