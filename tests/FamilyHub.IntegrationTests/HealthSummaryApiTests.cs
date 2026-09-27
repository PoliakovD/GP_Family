using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.HealthNotes;
using FamilyHub.Modules.Medical.MedicalRecords;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>Сводка хаба «Здоровье» (`GET /api/health/summary`) — smoke: свежий пользователь получает
/// пустые/нулевые блоки без ошибок, а реальные записи в дневнике/анализах/отчётах отражаются в сводке.
/// Подробности отбора данных внутри каждого блока покрыты юнит-тестами самих сервисов, которые
/// HealthSummaryService переиспользует (MedicationTodayService, HealthNoteService,
/// MedicalRecordService, ExtractionQueryService, VaccinationService, DoctorReportService).</summary>
public class HealthSummaryApiTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private static object DoctorReportBody(int? shareDays = 14) => new
    {
        periodFrom = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-6).ToString("yyyy-MM-dd"),
        periodTo = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
        includeLabs = true,
        includeAiSummaries = false,
        includeMedications = false,
        includeVisits = false,
        includeMeasurements = true,
        includeSymptomsNotes = false,
        includeVaccinations = false,
        recipient = (string?)null,
        patientComment = (string?)null,
        shareDays,
    };

    [Fact]
    public async Task Fresh_ReturnsEmptyDefaults_WithoutErrors()
    {
        var owner = ClientAs(FreshTelegramId());

        var response = await owner.GetAsync("/api/health/summary");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);

        summary.GetProperty("intake").GetProperty("total").GetInt32().Should().Be(0);
        summary.GetProperty("diary").GetProperty("latestBloodPressure").ValueKind.Should().Be(JsonValueKind.Null);
        summary.GetProperty("diary").GetProperty("latestSymptom").ValueKind.Should().Be(JsonValueKind.Null);
        summary.GetProperty("analyses").GetProperty("total").GetInt32().Should().Be(0);
        summary.GetProperty("visits").GetProperty("total").GetInt32().Should().Be(0);
        summary.GetProperty("indicators").GetProperty("trackedCount").GetInt32().Should().Be(0);
        summary.GetProperty("vaccinations").GetProperty("nextDue").ValueKind.Should().Be(JsonValueKind.Null);
        summary.GetProperty("reports").GetProperty("activeLinks").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task ReflectsDiaryEntry_OwnAnalysis_AndActiveReportLink()
    {
        var owner = ClientAs(FreshTelegramId());

        await owner.PostAsJsonAsync("/api/health-notes", new
        {
            kind = (int)HealthNoteKind.Metric,
            occurredAt = DateTime.UtcNow.AddHours(-2),
            metric = new { code = "blood_pressure", value = 128, value2 = 84 },
        });

        var record = await (await owner.PostAsJsonAsync("/api/medical-records",
                new CreateMedicalRecordRequest(DateOnly.FromDateTime(DateTime.UtcNow), "Смирнова А.И.", null, null)))
            .Content.ReadFromJsonAsync<MedicalRecordDto>(JsonOpts);

        (await owner.PostAsJsonAsync("/api/doctor-reports", DoctorReportBody()))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var summary = await owner.GetFromJsonAsync<JsonElement>("/api/health/summary", JsonOpts);

        var bp = summary.GetProperty("diary").GetProperty("latestBloodPressure");
        bp.GetProperty("value").GetDecimal().Should().Be(128m);
        bp.GetProperty("value2").GetDecimal().Should().Be(84m);

        var analyses = summary.GetProperty("analyses");
        analyses.GetProperty("total").GetInt32().Should().Be(1);
        analyses.GetProperty("latest").GetProperty("id").GetGuid().Should().Be(record!.Id);
        analyses.GetProperty("latest").GetProperty("doctor").GetString().Should().Be("Смирнова А.И.");

        summary.GetProperty("reports").GetProperty("activeLinks").GetInt32().Should().Be(1);
    }
}
