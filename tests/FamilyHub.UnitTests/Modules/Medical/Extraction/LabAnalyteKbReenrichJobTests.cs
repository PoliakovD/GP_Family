using FamilyHub.Domain.Entities;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.TestUtils;
using FluentAssertions;
using Hangfire;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>Принудительное переобогащение справочника после пересборки enrich-пайплайна (см. class
/// doc LabAnalyteKbReenrichJob) — отбирает строки со старой схемой и ставит форсированные задачи,
/// которые LabAnalyteEnrichmentProcessor не должен закрывать как "уже есть" без реальной работы.</summary>
public class LabAnalyteKbReenrichJobTests : SqliteTestBase
{
    // Пересборка enrich-пайплайна: источник — ссылка на GlobalSpecimenKb, не enum. Реальная
    // kb-запись никогда не имеет SpecimenKbId=Unresolved (жёсткий гейт в
    // LabAnalyteEnrichmentRequestService это гарантирует) — произвольные Guid'ы здесь просто
    // отличают "кровь" от "мочу" для теста, без реальных строк справочника.
    private static readonly Guid BloodId = Guid.NewGuid();
    private static readonly Guid UrineId = Guid.NewGuid();

    private readonly IBackgroundJobClient _backgroundJobs = Substitute.For<IBackgroundJobClient>();
    private readonly LabAnalyteKbReenrichJob _sut;

    public LabAnalyteKbReenrichJobTests()
    {
        var requestService = new LabAnalyteEnrichmentRequestService(
            Db, _backgroundJobs, NullLogger<LabAnalyteEnrichmentRequestService>.Instance);
        _sut = new LabAnalyteKbReenrichJob(Db, requestService, NullLogger<LabAnalyteKbReenrichJob>.Instance);
    }

    private void SeedKb(string normalizedName, Guid specimenKbId, int payloadVersion)
    {
        var now = DateTime.UtcNow;
        Db.GlobalLabAnalytesKb.Add(new GlobalLabAnalyteKb
        {
            Id = Guid.NewGuid(),
            NormalizedName = normalizedName,
            SpecimenKbId = specimenKbId,
            DisplayName = normalizedName,
            PayloadJson = "{}",
            PayloadVersion = payloadVersion,
            Source = "тест",
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    [Fact]
    public async Task RunAsync_StaleSchemaRow_CreatesForcedJob()
    {
        SeedKb("гемоглобин", BloodId, payloadVersion: 3);
        await Db.SaveChangesAsync();

        await _sut.RunAsync();

        var job = Db.LabAnalyteEnrichmentJobs.Single();
        job.NormalizedName.Should().Be("гемоглобин");
        job.SpecimenKbId.Should().Be(BloodId);
        job.Force.Should().BeTrue("иначе процессор увидит Hit в справочнике и завершит задачу без реальной работы");
        job.RequestedByUserId.Should().Be(Guid.Empty, "задача поставлена системой, не конкретным пользователем");
        _backgroundJobs.Received(1).Create(
            Arg.Is<Hangfire.Common.Job>(j => j.Method.Name == nameof(LabAnalyteEnrichmentProcessor.RunAsync)),
            Arg.Any<Hangfire.States.IState>());
    }

    [Fact]
    public async Task RunAsync_AlreadyCurrentSchemaRow_DoesNotCreateJob()
    {
        SeedKb("глюкоза", BloodId, payloadVersion: LabAnalyteSummarySchema.CurrentVersion);
        await Db.SaveChangesAsync();

        await _sut.RunAsync();

        Db.LabAnalyteEnrichmentJobs.Should().BeEmpty();
        _backgroundJobs.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task RunAsync_MoreStaleRowsThanBatchSize_OnlyProcessesOneBatch()
    {
        for (var i = 0; i < LabAnalyteKbReenrichJob.BatchSize + 5; i++)
            SeedKb($"показатель{i}", BloodId, payloadVersion: 1);
        await Db.SaveChangesAsync();

        await _sut.RunAsync();

        Db.LabAnalyteEnrichmentJobs.Count().Should().Be(LabAnalyteKbReenrichJob.BatchSize,
            "один прогон не должен забивать очередь enrichment разом — остаток дождётся следующего запуска");
    }

    [Fact]
    public async Task RunAsync_DifferentSpecimenSameName_BothGetSeparateForcedJobs()
    {
        SeedKb("белок", BloodId, payloadVersion: 3);
        SeedKb("белок", UrineId, payloadVersion: 3);
        await Db.SaveChangesAsync();

        await _sut.RunAsync();

        Db.LabAnalyteEnrichmentJobs.Should().HaveCount(2);
        Db.LabAnalyteEnrichmentJobs.Select(j => j.SpecimenKbId).Should().BeEquivalentTo([BloodId, UrineId]);
    }
}
