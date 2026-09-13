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
/// живой отчёт про запутывающий прогресс распознавания). Позиция считается по ОБЩЕЙ очереди к
/// единственному локальному LLM — по ВСЕМ четырём таблицам задач конвейера (не только
/// MedicalDocumentExtractionJobs — "extraction" и "enrichment" делят одну и ту же модель, см.
/// class doc LlmQueuePositionService/ExtractionQueryService.GetStatusAsync), не через Hangfire
/// IMonitoringApi. Раньше считалось только по своей таблице — систематически недооценивало
/// реальное ожидание под большим потоком задач обогащения справочника (баг, найденный на живом
/// отчёте).
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

    /// <summary>Считает Active-задачи (Pending/Running) по ВСЕМ четырём таблицам с CreatedAt
    /// строго раньше отсечки — тот же расчёт, что LlmQueuePositionService, но напрямую по БД, без
    /// зависимости от самого сервиса: тест должен проверять контракт эндпоинта, а не то, что
    /// сервис вызывает сам себя правильно. "Дельта", не абсолютное число — другие тесты этой же
    /// коллекции вполне могут оставить свои Active-задачи с более ранним CreatedAt (см.
    /// patterns/backend.md — тот же принцип, что у сид-хелперов).</summary>
    private static async Task<int> CountActiveAcrossAllTablesAsync(AppDbContext db, DateTime before)
    {
        var extraction = await db.MedicalDocumentExtractionJobs.CountAsync(
            j => j.CreatedAt < before && (j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running));
        var labAnalyte = await db.LabAnalyteEnrichmentJobs.CountAsync(
            j => j.CreatedAt < before && (j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running));
        var medication = await db.MedicationEnrichmentJobs.CountAsync(
            j => j.CreatedAt < before && (j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running));
        var visitMedication = await db.VisitMedicationEnrichmentJobs.CountAsync(
            j => j.CreatedAt < before && (j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running));
        return extraction + labAnalyte + medication + visitMedication;
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
        var baseline = await CountActiveAcrossAllTablesAsync(db, now);

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
    public async Task GetStatus_RunningJob_WithNothingAheadAnywhere_QueuePositionIsZero()
    {
        // Running и правда ничего не ждёт впереди — модель прямо сейчас работает над этой задачей.
        // Дельта, не абсолютный 0 — параллельно идущие тесты этой же коллекции вполне могут
        // оставить свои Active-задачи раньше `now` (та же оговорка, что и у соседних тестов).
        var owner = ClientAs(FreshTelegramId());
        var recordId = await CreateAnalysisRecordAsync(owner);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var baseline = await CountActiveAcrossAllTablesAsync(db, now);

        db.MedicalDocumentExtractionJobs.Add(new MedicalDocumentExtractionJob
        {
            Id = Guid.NewGuid(), MedicalRecordId = recordId, RequestedByUserId = Guid.NewGuid(),
            Status = EnrichmentJobStatus.Running, Stage = ExtractionStage.Ocr,
            TotalFiles = 1, CreatedAt = now, StartedAt = now,
        });
        await db.SaveChangesAsync();

        var response = await owner.GetAsync($"/api/medical-records/{recordId}/extraction");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await response.Content.ReadFromJsonAsync<ExtractionStatusResponse>();

        status!.Status.Should().Be(EnrichmentJobStatus.Running);
        (status.QueuePosition - baseline).Should().Be(0, "ничего НОВОГО не добавлено раньше `now` — сама задача не должна считать себя");
    }

    /// <summary>Регрессия для самого бага, найденного на живом отчёте: Hangfire дежурит
    /// "extraction" и "enrichment" РАЗНЫМИ серверами (см. Program.cs) — задача извлечения может
    /// стать Running (Hangfire её уже взял), пока модель прямо сейчас занята ДРУГИМ конвейером
    /// (здесь — обогащение справочника показателей). Раньше QueuePosition в этом случае молча
    /// был 0 (считался только по своей таблице) — пользователь видел "Читаем текст" и не понимал,
    /// почему прогресс не двигается. Теперь позиция отражает реальную очередь к общей модели.</summary>
    [Fact]
    public async Task GetStatus_RunningJob_WithEarlierActiveJobInAnotherPipeline_QueuePositionIsNotZero()
    {
        var owner = ClientAs(FreshTelegramId());
        var recordId = await CreateAnalysisRecordAsync(owner);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var baseline = await CountActiveAcrossAllTablesAsync(db, now);

        // Задача обогащения справочника показателей — ДРУГОЙ конвейер, ДРУГАЯ таблица, но та же
        // единственная локальная модель (LmStudioConcurrencyGate) — реально держит гейт, пока
        // наша задача извлечения просто дожидается своей очереди.
        db.LabAnalyteEnrichmentJobs.Add(new LabAnalyteEnrichmentJob
        {
            Id = Guid.NewGuid(), NormalizedName = "тест", SpecimenKbId = Guid.NewGuid(), SourceDisplayName = "Тест",
            RequestedByUserId = Guid.NewGuid(), Status = EnrichmentJobStatus.Running, CreatedAt = now.AddSeconds(-10),
        });

        db.MedicalDocumentExtractionJobs.Add(new MedicalDocumentExtractionJob
        {
            Id = Guid.NewGuid(), MedicalRecordId = recordId, RequestedByUserId = Guid.NewGuid(),
            Status = EnrichmentJobStatus.Running, Stage = ExtractionStage.Ocr,
            TotalFiles = 1, CreatedAt = now, StartedAt = now,
        });
        await db.SaveChangesAsync();

        var response = await owner.GetAsync($"/api/medical-records/{recordId}/extraction");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await response.Content.ReadFromJsonAsync<ExtractionStatusResponse>();

        status!.Status.Should().Be(EnrichmentJobStatus.Running);
        (status.QueuePosition - baseline).Should().Be(1,
            "задача обогащения показателей из ДРУГОЙ таблицы, но с более ранним CreatedAt, реально держит " +
            "гейт LM Studio — Stage=Ocr здесь не значит, что модель отвечает именно по этой задаче");
    }
}
