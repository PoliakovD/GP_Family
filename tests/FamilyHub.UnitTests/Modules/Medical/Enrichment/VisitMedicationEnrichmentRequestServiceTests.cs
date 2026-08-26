using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.Enrichment;
using FamilyHub.TestUtils;
using FluentAssertions;
using Hangfire;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Enrichment;

/// <summary>UX-редизайн: обогащение справочника медикаментов для препарата, упомянутого в
/// заключении врача — у визита нет FamilyId (в отличие от аптечки), поэтому отдельная таблица
/// задач (см. class doc VisitMedicationEnrichmentJob). Проверяем именно новую логику: мягкий
/// дедуп против уже идущей задачи семейного конвейера (EnrichmentRequestService) для того же
/// препарата — постановка второй задачи была бы лишним платным внешним запросом.</summary>
public class VisitMedicationEnrichmentRequestServiceTests : SqliteTestBase
{
    private readonly IBackgroundJobClient _backgroundJobs = Substitute.For<IBackgroundJobClient>();
    private readonly VisitMedicationEnrichmentRequestService _sut;

    public VisitMedicationEnrichmentRequestServiceTests()
    {
        _sut = new VisitMedicationEnrichmentRequestService(
            Db, _backgroundJobs, NullLogger<VisitMedicationEnrichmentRequestService>.Instance);
    }

    [Fact]
    public async Task RequestAsync_NoExistingJob_CreatesPendingJobAndEnqueues()
    {
        var userId = Guid.NewGuid();
        var recordId = Guid.NewGuid();

        await _sut.RequestAsync("парацетамол", "Парацетамол", recordId, userId);

        var job = Db.VisitMedicationEnrichmentJobs.Single();
        job.NormalizedName.Should().Be("парацетамол");
        job.SourceDisplayName.Should().Be("Парацетамол");
        job.MedicalRecordId.Should().Be(recordId);
        job.RequestedByUserId.Should().Be(userId);
        job.Status.Should().Be(EnrichmentJobStatus.Pending);
        _backgroundJobs.Received(1).Create(
            Arg.Is<Hangfire.Common.Job>(j => j.Method.Name == nameof(VisitMedicationEnrichmentProcessor.RunAsync)),
            Arg.Any<Hangfire.States.IState>());
    }

    [Fact]
    public async Task RequestAsync_SameNamePendingInFamilyMedicationPipeline_DoesNotCreateDuplicateJob()
    {
        // Кто-то уже добавил тот же препарат в аптечку прямо сейчас — обогащение уже идёт через
        // семейный конвейер (MedicationEnrichmentJobs), отдельной задачи здесь заводить не нужно.
        Db.MedicationEnrichmentJobs.Add(new MedicationEnrichmentJob
        {
            Id = Guid.NewGuid(),
            NormalizedName = "парацетамол",
            SourceDisplayName = "Парацетамол",
            RequestedByUserId = Guid.NewGuid(),
            FamilyId = Guid.NewGuid(),
            Status = EnrichmentJobStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        await _sut.RequestAsync("парацетамол", "Парацетамол", Guid.NewGuid(), Guid.NewGuid());

        Db.VisitMedicationEnrichmentJobs.Should().BeEmpty();
        _backgroundJobs.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task RequestAsync_SameNameCompletedInFamilyMedicationPipeline_StillCreatesJob()
    {
        // Задача аптечки уже ЗАВЕРШЕНА (не Pending/Running) — значит либо справочник уже пополнен
        // (и наш собственный KbLookupService поймает это раньше вызова RequestAsync), либо попытка
        // не удалась и имеет смысл попробовать снова, а не молчать вечно.
        Db.MedicationEnrichmentJobs.Add(new MedicationEnrichmentJob
        {
            Id = Guid.NewGuid(),
            NormalizedName = "парацетамол",
            SourceDisplayName = "Парацетамол",
            RequestedByUserId = Guid.NewGuid(),
            FamilyId = Guid.NewGuid(),
            Status = EnrichmentJobStatus.Failed,
            CreatedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        await _sut.RequestAsync("парацетамол", "Парацетамол", Guid.NewGuid(), Guid.NewGuid());

        Db.VisitMedicationEnrichmentJobs.Should().ContainSingle();
    }
}
