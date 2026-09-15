using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.MedicalRecords;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Лимиты пайплайна на пользователя (ExtractionLimitsOptions, батч-загрузка, см. .claude/plans/
/// ethereal-hugging-chipmunk.md, часть 1.3) — POST .../extract сквозь настоящий HTTP-конвейер
/// (в отличие от ExtractionRequestServiceLimitsTests, который бьёт по сервису напрямую на SQLite).
/// Дефолты класса (не переопределяются конфигом теста, см. FamilyHubWebFactory) — тест подгоняет
/// количество засеянных задач ПОД реальный сконфигурированный лимит, читая его из DI, а не
/// дублирует магическое число 20.
/// </summary>
public class ExtractionLimitsApiTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private static async Task<MedicalRecordDto> CreateAnalysisRecordAsync(HttpClient owner)
    {
        var response = await owner.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest(DateOnly.FromDateTime(DateTime.UtcNow), null, null, null));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<MedicalRecordDto>(JsonOpts))!;
    }

    /// <summary>Своя, ещё не занятая (частичный уникальный индекс по MedicalRecordId) Pending-задача
    /// на произвольную запись того же владельца — MedicalRecordId FK-less, реальная строка
    /// MedicalRecords не нужна для одной только проверки лимита активных задач (см. class doc
    /// ExtractionRequestService.RequestAsync — лимит проверяется ДО обращения к вложениям).</summary>
    private static async Task SeedActiveJobAsync(AppDbContext db, Guid ownerUserId)
    {
        db.MedicalDocumentExtractionJobs.Add(new MedicalDocumentExtractionJob
        {
            Id = Guid.NewGuid(),
            MedicalRecordId = Guid.NewGuid(),
            RequestedByUserId = ownerUserId,
            Status = EnrichmentJobStatus.Pending,
            Stage = ExtractionStage.Queued,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task RequestExtraction_ActiveJobsAtConfiguredLimit_Returns429TooManyActiveJobs()
    {
        var owner = ClientAs(FreshTelegramId());
        var record = await CreateAnalysisRecordAsync(owner);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var limits = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ExtractionLimitsOptions>>().Value;

        for (var i = 0; i < limits.MaxActiveJobsPerUser; i++)
            await SeedActiveJobAsync(db, record.OwnerUserId);

        var response = await owner.PostAsync($"/api/medical-records/{record.Id}/extract", null);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("too_many_active_jobs");
    }

    [Fact]
    public async Task RequestExtraction_BelowActiveJobsLimit_DoesNotReturn429()
    {
        var owner = ClientAs(FreshTelegramId());
        var record = await CreateAnalysisRecordAsync(owner);

        var response = await owner.PostAsync($"/api/medical-records/{record.Id}/extract", null);

        // NothingToDo (409) — запись без вложений, но НЕ 429: сам лимит не сработал.
        response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task GetExtractionLimits_ReflectsCurrentActiveJobsCount()
    {
        var owner = ClientAs(FreshTelegramId());
        var record = await CreateAnalysisRecordAsync(owner);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await SeedActiveJobAsync(db, record.OwnerUserId);

        var response = await owner.GetAsync("/api/medical-records/extraction-limits");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ExtractionLimitsDto>(JsonOpts);

        body!.ActiveNow.Should().BeGreaterThanOrEqualTo(1);
    }
}
