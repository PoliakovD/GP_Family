using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.DoctorReports;
using FamilyHub.Modules.Medical.Extraction;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical;

public class DoctorReportHtmlRendererTests
{
    private static readonly ReportBlocks All = new(true, true, true, true, true, true);

    private static ReportModel Model(
        ReportBlocks? blocks = null, string? comment = "Слабость", IReadOnlyList<ReportNote>? flagged = null,
        ReportLabTable? labs = null, IReadOnlyList<ReportSummaryItem>? summaries = null,
        IReadOnlyList<ReportMedication>? meds = null, IReadOnlyList<ReportVisit>? visits = null,
        IReadOnlyList<ReportMetric>? metrics = null, IReadOnlyList<ReportSymptom>? symptoms = null,
        IReadOnlyList<ReportNote>? notes = null, int skipped = 0) =>
        new(new ReportPatient("Поляков Даниил Александрович", "Поляков Д.А.", "м", new DateOnly(1998, 5, 4)),
            new DateOnly(2026, 3, 26), new DateOnly(2026, 9, 26), new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc),
            blocks ?? All, comment, flagged ?? [], labs, summaries, skipped, meds, visits, metrics, null, null, symptoms, notes);

    [Fact]
    public void Header_HasPatientPeriodAgeAndDisclaimer()
    {
        var html = DoctorReportHtmlRenderer.Render(Model());

        html.Should().Contain("Отчёт для врача")
            .And.Contain("Поляков Даниил Александрович")
            .And.Contain("26.03.2026 — 26.09.2026")
            .And.Contain("28 лет")
            .And.Contain("04.05.1998")
            .And.Contain("Не является официальным медицинским документом");
    }

    [Fact]
    public void UserText_IsHtmlEncoded()
    {
        var html = DoctorReportHtmlRenderer.Render(Model(
            comment: "<script>alert(1)</script> & \"кавычки\"",
            flagged: [new ReportNote(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), "<b>жирный</b>")]));

        html.Should().NotContain("<script>").And.NotContain("<b>жирный</b>");
        html.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;").And.Contain("&amp;").And.Contain("&lt;b&gt;жирный&lt;/b&gt;");
    }

    [Fact]
    public void Comment_KeepsLineBreaks()
    {
        var html = DoctorReportHtmlRenderer.Render(Model(comment: "первая\nвторая"));

        html.Should().Contain("первая<br>вторая");
    }

    [Fact]
    public void Sections_FollowEnabledBlocks_AndAreNumberedInOrder()
    {
        var model = Model(blocks: new ReportBlocks(true, false, true, false, true, false), flagged: [new ReportNote(DateTime.UtcNow, "вопрос")]);

        DoctorReportHtmlRenderer.Sections(model).Should().Equal(
            "Жалобы и вопросы пациента", "Препараты", "Динамика анализов", "Домашние замеры");

        var html = DoctorReportHtmlRenderer.Render(model);
        html.Should().Contain("<span class=\"num-badge\">1.</span> Жалобы и вопросы пациента")
            .And.Contain("<span class=\"num-badge\">2.</span> Препараты")
            .And.Contain("<span class=\"num-badge\">4.</span> Домашние замеры")
            .And.NotContain("Резюме анализов (ИИ)").And.NotContain("Приёмы врача");
    }

    [Fact]
    public void NoComplaints_MeansNoComplaintsSection()
    {
        DoctorReportHtmlRenderer.Sections(Model(comment: null)).Should().NotContain("Жалобы и вопросы пациента");
    }

    [Fact]
    public void EnabledBlockWithoutData_ShowsEmptyNoteInsteadOfBeingHidden()
    {
        var html = DoctorReportHtmlRenderer.Render(Model(labs: new ReportLabTable([], [], 0), visits: []));

        html.Should().Contain("Показателей анализов с динамикой или отклонениями за период нет.")
            .And.Contain("Приёмов врача за период нет.");
    }

    [Fact]
    public void LabTable_MarksDeviationsWithArrowsAndClasses()
    {
        var d1 = new DateOnly(2026, 5, 10);
        var d2 = new DateOnly(2026, 8, 10);
        var labs = new ReportLabTable([d1, d2],
        [
            new ReportLabRow("Гемоглобин", "г/л", "130–160",
                [new ReportLabCell(d1, "118", IndicatorFlag.Low), new ReportLabCell(d2, "134", IndicatorFlag.Normal)], true),
            new ReportLabRow("Лейкоциты", null, null, [new ReportLabCell(d2, "12", IndicatorFlag.High)], true),
        ], 3);

        var html = DoctorReportHtmlRenderer.Render(Model(labs: labs));

        html.Should().Contain("<span class=\"lo\">118 ↓</span>")
            .And.Contain("<span>134</span>")
            .And.Contain("<span class=\"hi\">12 ↑</span>")
            .And.Contain("130–160")
            .And.Contain("Ещё 3 показателя в норме не показаны");
    }

    [Fact]
    public void Metrics_ShowPairedValuesAndSparkline()
    {
        var metric = new ReportMetric("blood_pressure", "Давление", "мм рт. ст.", 3,
            120, 130, 125, 125, new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc), 80, 86, 82, 82, [120, 130, 125]);

        var html = DoctorReportHtmlRenderer.Render(Model(metrics: [metric]));

        html.Should().Contain("120–130 / 80–86").And.Contain("125/82").And.Contain("<svg").And.Contain("<polyline");
    }

    [Fact]
    public void Medications_ShowPrescriptionAndDiaryIntakeSources()
    {
        var meds = new[]
        {
            new ReportMedication("Сорбифер", "1 таб. 2 раза в день", new DateOnly(2026, 8, 12), "Смирнова", 2, new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc)),
            new ReportMedication("Нурофен", "200 мг", null, null, 1, new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc)),
        };

        var html = DoctorReportHtmlRenderer.Render(Model(meds: meds));

        html.Should().Contain("назначил(а) Смирнова, 12.08.2026")
            .And.Contain("по дневнику: 2 приёма, последний 02.09.2026")
            .And.Contain("по дневнику: 1 приём, последний 03.09.2026");
    }

    [Fact]
    public void Summaries_ShowSkippedNote()
    {
        var item = new ReportSummaryItem(new DateOnly(2026, 5, 1), "ОАК", "Всё хорошо", [new LabSummaryDeviation("Ферритин", "снижен")]);

        var html = DoctorReportHtmlRenderer.Render(Model(summaries: [item], skipped: 2));

        html.Should().Contain("Всё хорошо").And.Contain("<b>Ферритин</b> — снижен").And.Contain("Ещё 2 резюме не включено");
    }

    [Fact]
    public void Footer_HasPageNumberPlaceholdersAndPatient()
    {
        var footer = DoctorReportHtmlRenderer.RenderFooter(Model());

        footer.Should().Contain("class=\"pageNumber\"").And.Contain("class=\"totalPages\"").And.Contain("Поляков Д.А.");
    }
}
