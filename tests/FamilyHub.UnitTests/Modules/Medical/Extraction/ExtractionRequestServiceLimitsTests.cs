using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.TestUtils;
using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>
/// Лимиты на пользователя (ExtractionLimitsOptions, батч-загрузка) — TooManyActiveJobs/
/// DailyQuotaExceeded, добавленные в ExtractionRequestService.RequestAsync. Оба лимита мягкие
/// (см. class doc сервиса) — здесь проверяется сам факт срабатывания порога, не устойчивость к
/// гонке параллельных вызовов (та защищена отдельно уникальным индексом + catch DbUpdateException,
/// уже покрытым существующим поведением AlreadyQueued).
/// </summary>
public class ExtractionRequestServiceLimitsTests : SqliteTestBase
{
    private readonly IBackgroundJobClient _backgroundJobs = Substitute.For<IBackgroundJobClient>();
    private readonly ILmStudioAvailabilityProbe _probe = Substitute.For<ILmStudioAvailabilityProbe>();

    public ExtractionRequestServiceLimitsTests()
    {
        // По умолчанию ИИ доступен — иначе постановка уходила бы в «ждёт ИИ» и не в Hangfire.
        _probe.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
    }

    private ExtractionRequestService CreateSut(ExtractionLimitsOptions limits) =>
        new(Db, _backgroundJobs, _probe, Options.Create(limits), NullLogger<ExtractionRequestService>.Instance);

    /// <summary>ИИ недоступен: задача создаётся (пользователь ничего не теряет), помечается
    /// WaitingForAi и НЕ уходит в Hangfire — её запустит LmStudioRecoverySweepJob, когда сервер вернётся.</summary>
    [Fact]
    public async Task RequestAsync_AiUnavailable_CreatesWaitingJob_WithoutEnqueuing()
    {
        var userId = Guid.NewGuid();
        var recordId = SeedRecordWithPendingAttachment(userId);
        _probe.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);
        var sut = CreateSut(new ExtractionLimitsOptions { MaxActiveJobsPerUser = 5, DailyJobsPerUser = 100 });

        var result = await sut.RequestAsync(recordId, userId);

        result.Should().Be(ExtractionRequestResult.QueuedWaitingForAi);
        var job = Db.MedicalDocumentExtractionJobs.Single(j => j.MedicalRecordId == recordId);
        job.Status.Should().Be(EnrichmentJobStatus.Pending);
        job.WaitingForAi.Should().BeTrue();
        _backgroundJobs.ReceivedCalls().Should().BeEmpty("в очередь Hangfire задача попадёт только через sweep, когда ИИ вернётся");
        Db.MedicalRecords.AsNoTracking().Single(r => r.Id == recordId).ExtractionStatus.Should().Be(ExtractionStatus.Pending);
    }

    /// <summary>Запись + одно необработанное вложение — минимум, необходимый RequestAsync, чтобы
    /// дойти ДО проверки лимитов (NotFound/Forbidden/NothingToDo проверяются раньше в коде).</summary>
    private Guid SeedRecordWithPendingAttachment(Guid ownerUserId)
    {
        var recordId = Guid.NewGuid();
        Db.MedicalRecords.Add(new MedicalRecord
        {
            Id = recordId,
            OwnerUserId = ownerUserId,
            Kind = MedicalRecordKind.Analysis,
            RecordDate = DateOnly.FromDateTime(DateTime.UtcNow),
            ExtractionStatus = ExtractionStatus.None,
            CreatedAt = DateTime.UtcNow,
        });
        Db.FileAttachments.Add(new FileAttachment
        {
            Id = Guid.NewGuid(),
            OwnerType = FileOwnerType.MedicalRecord,
            OwnerId = recordId,
            FileName = "scan.pdf",
            ContentType = "application/pdf",
            SizeBytes = 100,
            StorageKey = Guid.NewGuid().ToString(),
            UploadedAt = DateTime.UtcNow,
            ExtractedAt = null,
        });
        Db.SaveChanges();
        return recordId;
    }

    private void SeedJob(Guid userId, Guid recordId, EnrichmentJobStatus status, DateTime createdAt)
    {
        Db.MedicalDocumentExtractionJobs.Add(new MedicalDocumentExtractionJob
        {
            Id = Guid.NewGuid(),
            MedicalRecordId = recordId,
            RequestedByUserId = userId,
            Status = status,
            Stage = ExtractionStage.Queued,
            CreatedAt = createdAt,
        });
        Db.SaveChanges();
    }

    [Fact]
    public async Task RequestAsync_ActiveJobsAtLimit_ReturnsTooManyActiveJobs()
    {
        var userId = Guid.NewGuid();
        // Активные задачи пользователя — на ДРУГИХ записях (дедуп по MedicalRecordId не должен
        // маскировать лимит на пользователя целиком).
        for (var i = 0; i < 2; i++)
        {
            var otherRecordId = SeedRecordWithPendingAttachment(userId);
            SeedJob(userId, otherRecordId, EnrichmentJobStatus.Pending, DateTime.UtcNow);
        }
        var recordId = SeedRecordWithPendingAttachment(userId);
        var sut = CreateSut(new ExtractionLimitsOptions { MaxActiveJobsPerUser = 2, DailyJobsPerUser = 100 });

        var result = await sut.RequestAsync(recordId, userId);

        result.Should().Be(ExtractionRequestResult.TooManyActiveJobs);
    }

    [Fact]
    public async Task RequestAsync_ActiveJobsBelowLimit_Succeeds()
    {
        var userId = Guid.NewGuid();
        var otherRecordId = SeedRecordWithPendingAttachment(userId);
        SeedJob(userId, otherRecordId, EnrichmentJobStatus.Pending, DateTime.UtcNow);
        var recordId = SeedRecordWithPendingAttachment(userId);
        var sut = CreateSut(new ExtractionLimitsOptions { MaxActiveJobsPerUser = 2, DailyJobsPerUser = 100 });

        var result = await sut.RequestAsync(recordId, userId);

        result.Should().Be(ExtractionRequestResult.Success);
    }

    [Fact]
    public async Task RequestAsync_CompletedJobsDoNotCountTowardsActiveLimit()
    {
        var userId = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
        {
            var otherRecordId = SeedRecordWithPendingAttachment(userId);
            SeedJob(userId, otherRecordId, EnrichmentJobStatus.Completed, DateTime.UtcNow);
        }
        var recordId = SeedRecordWithPendingAttachment(userId);
        var sut = CreateSut(new ExtractionLimitsOptions { MaxActiveJobsPerUser = 1, DailyJobsPerUser = 100 });

        var result = await sut.RequestAsync(recordId, userId);

        result.Should().Be(ExtractionRequestResult.Success,
            "Completed/Failed-задачи не должны занимать место в лимите активных — только Pending/Running");
    }

    [Fact]
    public async Task RequestAsync_DailyQuotaReached_ReturnsDailyQuotaExceeded()
    {
        var userId = Guid.NewGuid();
        var todayJobsCount = 3;
        for (var i = 0; i < todayJobsCount; i++)
        {
            var otherRecordId = SeedRecordWithPendingAttachment(userId);
            // Completed — не считаются активными задачами, но всё равно расходуют дневную квоту
            // (квота — по факту ПОСТАНОВКИ в очередь, не по текущему статусу).
            SeedJob(userId, otherRecordId, EnrichmentJobStatus.Completed, DateTime.UtcNow);
        }
        var recordId = SeedRecordWithPendingAttachment(userId);
        var sut = CreateSut(new ExtractionLimitsOptions { MaxActiveJobsPerUser = 100, DailyJobsPerUser = todayJobsCount });

        var result = await sut.RequestAsync(recordId, userId);

        result.Should().Be(ExtractionRequestResult.DailyQuotaExceeded);
    }

    [Fact]
    public async Task RequestAsync_JobsFromYesterday_DoNotCountTowardsDailyQuota()
    {
        var userId = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
        {
            var otherRecordId = SeedRecordWithPendingAttachment(userId);
            SeedJob(userId, otherRecordId, EnrichmentJobStatus.Completed, DateTime.UtcNow.AddDays(-1));
        }
        var recordId = SeedRecordWithPendingAttachment(userId);
        var sut = CreateSut(new ExtractionLimitsOptions { MaxActiveJobsPerUser = 100, DailyJobsPerUser = 1 });

        var result = await sut.RequestAsync(recordId, userId);

        result.Should().Be(ExtractionRequestResult.Success, "квота считается по календарным суткам UTC, вчерашние задачи не в счёт");
    }

    [Fact]
    public async Task GetLimitsAsync_ReturnsCurrentUsageAgainstConfiguredLimits()
    {
        var userId = Guid.NewGuid();
        var recordId = SeedRecordWithPendingAttachment(userId);
        SeedJob(userId, recordId, EnrichmentJobStatus.Pending, DateTime.UtcNow);
        var sut = CreateSut(new ExtractionLimitsOptions { MaxBatchDocuments = 20, MaxActiveJobsPerUser = 10, DailyJobsPerUser = 50 });

        var limits = await sut.GetLimitsAsync(userId);

        limits.MaxBatchDocuments.Should().Be(20);
        limits.MaxActiveJobs.Should().Be(10);
        limits.ActiveNow.Should().Be(1);
        limits.DailyQuota.Should().Be(50);
        limits.UsedToday.Should().Be(1);
    }
}
