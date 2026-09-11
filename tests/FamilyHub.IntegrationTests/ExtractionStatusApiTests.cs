using System.Net;
using System.Net.Http.Json;
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
/// GET /api/medical-records/{id}/extraction — QueuePosition (UI-редизайн: "какой я в очереди",
/// живой отчёт про запутывающий прогресс распознавания). Очередь "extraction" общая на всех
/// пользователей/записи (один воркер, LM Studio — один ноутбук за WireGuard) — позиция считается
/// напрямую по таблице задач (см. ExtractionQueryService.GetStatusAsync), не через Hangfire
/// IMonitoringApi.
/// </summary>
public class ExtractionStatusApiTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private static async Task<Guid> CreateAnalysisRecordAsync(HttpClient owner)
    {
        var response = await owner.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest(DateOnly.FromDateTime(DateTime.UtcNow), null, null, null));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var record = (await response.Content.ReadFromJsonAsync<MedicalRecordDto>())!;
        return record.Id;
    }

    /// <summary>Заводит минимальную запись + Pending-задачу извлечения напрямую через AppDbContext —
    /// тест бьёт по подсчёту очереди самим GetStatusAsync, не по постановке в очередь через
    /// ExtractionRequestService (частичный уникальный индекс всё равно не даст завести две живые
    /// задачи на одну запись, поэтому "конкурирующие" задачи очереди — разные записи, как и в
    /// реальности: очередь общая на всех пользователей).</summary>
    private async Task<Guid> SeedPendingJobAsync(AppDbContext db, DateTime createdAt)
    {
        var recordId = Guid.NewGuid();
        db.MedicalRecords.Add(new MedicalRecord
        {
            Id = recordId, OwnerUserId = Guid.NewGuid(), Kind = MedicalRecordKind.Analysis,
            RecordDate = DateOnly.FromDateTime(DateTime.UtcNow), ExtractionStatus = ExtractionStatus.Pending,
            CreatedAt = createdAt,
        });
        db.MedicalDocumentExtractionJobs.Add(new MedicalDocumentExtractionJob
        {
            Id = Guid.NewGuid(), MedicalRecordId = recordId, RequestedByUserId = Guid.NewGuid(),
            Status = EnrichmentJobStatus.Pending, Stage = ExtractionStage.Queued, CreatedAt = createdAt,
        });
        await db.SaveChangesAsync();
        return recordId;
    }

    [Fact]
    public async Task GetStatus_PendingJob_QueuePosition_CountsOnlyEarlierPendingJobs()
    {
        var owner = ClientAs(FreshTelegramId());
        var recordId = await CreateAnalysisRecordAsync(owner);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        // Очередь общая на всю (реальную для интеграционных тестов) БД — считаем ДЕЛЬТУ, не
        // абсолютное число: другие тесты этой же коллекции вполне могут оставить свои Pending-задачи
        // с CreatedAt раньше `now` (см. patterns/backend.md — тот же принцип, что у сид-хелперов).
        var baseline = await db.MedicalDocumentExtractionJobs
            .CountAsync(j => j.Status == EnrichmentJobStatus.Pending && j.CreatedAt < now);

        // Три Pending-задачи ДРУГИХ записей раньше нашей — должны попасть в позицию.
        await SeedPendingJobAsync(db, now.AddSeconds(-30));
        await SeedPendingJobAsync(db, now.AddSeconds(-20));
        await SeedPendingJobAsync(db, now.AddSeconds(-10));
        // Одна Pending-задача ПОЗЖЕ нашей — не должна учитываться (она сама за нами в очереди).
        var laterRecordId = Guid.NewGuid();
        db.MedicalRecords.Add(new MedicalRecord
        {
            Id = laterRecordId, OwnerUserId = Guid.NewGuid(), Kind = MedicalRecordKind.Analysis,
            RecordDate = DateOnly.FromDateTime(DateTime.UtcNow), ExtractionStatus = ExtractionStatus.Pending,
            CreatedAt = now.AddSeconds(30),
        });
        db.MedicalDocumentExtractionJobs.Add(new MedicalDocumentExtractionJob
        {
            Id = Guid.NewGuid(), MedicalRecordId = laterRecordId, RequestedByUserId = Guid.NewGuid(),
            Status = EnrichmentJobStatus.Pending, Stage = ExtractionStage.Queued, CreatedAt = now.AddSeconds(30),
        });

        // Задача, которую мы будем опрашивать — Pending, между первыми тремя и последней.
        var job = new MedicalDocumentExtractionJob
        {
            Id = Guid.NewGuid(), MedicalRecordId = recordId, RequestedByUserId = Guid.NewGuid(),
            Status = EnrichmentJobStatus.Pending, Stage = ExtractionStage.Queued, CreatedAt = now,
        };
        db.MedicalDocumentExtractionJobs.Add(job);
        await db.SaveChangesAsync();

        var response = await owner.GetAsync($"/api/medical-records/{recordId}/extraction");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await response.Content.ReadFromJsonAsync<ExtractionStatusResponse>();

        (status!.QueuePosition - baseline).Should().Be(3,
            "впереди ровно три НАШИ Pending-задачи с более ранним CreatedAt, четвёртая (более поздняя) не считается");
    }

    [Fact]
    public async Task GetStatus_RunningJob_QueuePositionIsZero()
    {
        // Позиция бессмысленна для уже начатой задачи — фронт её не показывает, но контракт должен
        // отдавать 0, не какое-то случайное число из подсчёта.
        var owner = ClientAs(FreshTelegramId());
        var recordId = await CreateAnalysisRecordAsync(owner);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Другая Pending-задача раньше по времени — если бы позиция считалась не глядя на статус
        // ЭТОЙ задачи, она бы просочилась в QueuePosition.
        await SeedPendingJobAsync(db, DateTime.UtcNow.AddMinutes(-1));

        db.MedicalDocumentExtractionJobs.Add(new MedicalDocumentExtractionJob
        {
            Id = Guid.NewGuid(), MedicalRecordId = recordId, RequestedByUserId = Guid.NewGuid(),
            Status = EnrichmentJobStatus.Running, Stage = ExtractionStage.Ocr,
            TotalFiles = 1, CreatedAt = DateTime.UtcNow, StartedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var response = await owner.GetAsync($"/api/medical-records/{recordId}/extraction");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await response.Content.ReadFromJsonAsync<ExtractionStatusResponse>();

        status!.Status.Should().Be(EnrichmentJobStatus.Running);
        status.QueuePosition.Should().Be(0);
    }
}
