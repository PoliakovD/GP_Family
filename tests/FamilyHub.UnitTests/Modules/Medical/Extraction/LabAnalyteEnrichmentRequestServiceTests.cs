using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.TestUtils;
using FluentAssertions;
using Hangfire;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>
/// Единственная точка входа обогащения справочника показателей. Проверяем в первую очередь новый
/// гейт "уже проваливалось" — до него частичный уникальный индекс дедупил только ПОКА задача жива
/// (Pending/Running/Deferred), а Failed из-под фильтра выпадал: повторное извлечение того же
/// документа (или новый документ с тем же показателем) заводило новую Failed-задачу с той же
/// причиной на каждый прогон — найдено на проде как "куча одинаковых карточек в «Требует внимания»".
/// </summary>
public class LabAnalyteEnrichmentRequestServiceTests : SqliteTestBase
{
    private readonly IBackgroundJobClient _backgroundJobs = Substitute.For<IBackgroundJobClient>();
    private readonly LabAnalyteEnrichmentRequestService _sut;

    public LabAnalyteEnrichmentRequestServiceTests()
    {
        _sut = new LabAnalyteEnrichmentRequestService(Db, _backgroundJobs, NullLogger<LabAnalyteEnrichmentRequestService>.Instance);
    }

    private static readonly Guid SpecimenId = Guid.NewGuid();

    [Fact]
    public async Task RequestAsync_NoExistingJob_CreatesPendingJobAndEnqueues()
    {
        await _sut.RequestAsync("гемоглобин", SpecimenId, "Гемоглобин", null, Guid.NewGuid());

        var job = Db.LabAnalyteEnrichmentJobs.Single();
        job.NormalizedName.Should().Be("гемоглобин");
        job.SpecimenKbId.Should().Be(SpecimenId);
        job.Status.Should().Be(EnrichmentJobStatus.Pending);
        _backgroundJobs.Received(1).Create(
            Arg.Is<Hangfire.Common.Job>(j => j.Method.Name == nameof(LabAnalyteEnrichmentProcessor.RunAsync)),
            Arg.Any<Hangfire.States.IState>());
    }

    [Fact]
    public async Task RequestAsync_SameKeyAlreadyFailed_DoesNotCreateDuplicateJob()
    {
        Db.LabAnalyteEnrichmentJobs.Add(new LabAnalyteEnrichmentJob
        {
            Id = Guid.NewGuid(), NormalizedName = "гемоглобин", SpecimenKbId = SpecimenId,
            SourceDisplayName = "Гемоглобин", RequestedByUserId = Guid.NewGuid(),
            Status = EnrichmentJobStatus.Failed, FailureReason = EnrichmentFailureReason.NoTrustedSnippets,
            CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        await _sut.RequestAsync("гемоглобин", SpecimenId, "Гемоглобин", null, Guid.NewGuid());

        Db.LabAnalyteEnrichmentJobs.Should().ContainSingle("новая задача не должна создаваться поверх уже проваленной");
        _backgroundJobs.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task RequestAsync_SameKeySkippedLegacyStatus_DoesNotCreateDuplicateJob()
    {
        // Skipped — легаси-статус удалённой месячной квоты (ADR-0005 §9), старые строки всё ещё
        // могут существовать в БД — та же "известный неудачный исход", что и Failed.
        Db.LabAnalyteEnrichmentJobs.Add(new LabAnalyteEnrichmentJob
        {
            Id = Guid.NewGuid(), NormalizedName = "гемоглобин", SpecimenKbId = SpecimenId,
            SourceDisplayName = "Гемоглобин", RequestedByUserId = Guid.NewGuid(),
            Status = EnrichmentJobStatus.Skipped, CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        await _sut.RequestAsync("гемоглобин", SpecimenId, "Гемоглобин", null, Guid.NewGuid());

        Db.LabAnalyteEnrichmentJobs.Should().ContainSingle();
    }

    [Fact]
    public async Task RequestAsync_DifferentSpecimen_SameName_StillCreatesJob()
    {
        // Ключ дедупа — пара (название, источник) — "белок" в крови и в моче не должны мешать
        // друг другу, даже если один из них уже проваливался.
        Db.LabAnalyteEnrichmentJobs.Add(new LabAnalyteEnrichmentJob
        {
            Id = Guid.NewGuid(), NormalizedName = "белок", SpecimenKbId = SpecimenId,
            SourceDisplayName = "Белок", RequestedByUserId = Guid.NewGuid(),
            Status = EnrichmentJobStatus.Failed, CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        var otherSpecimen = Guid.NewGuid();
        await _sut.RequestAsync("белок", otherSpecimen, "Белок", null, Guid.NewGuid());

        Db.LabAnalyteEnrichmentJobs.Should().HaveCount(2);
    }

    [Fact]
    public async Task RequestAsync_Force_BypassesFailedGate_CreatesNewJob()
    {
        // force=true — переобогащение (LabAnalyteKbReenrichJob/ручной reenrich из админки),
        // цель ИМЕННО повторить попытку — гейт "уже проваливалось" здесь должен молчать.
        Db.LabAnalyteEnrichmentJobs.Add(new LabAnalyteEnrichmentJob
        {
            Id = Guid.NewGuid(), NormalizedName = "гемоглобин", SpecimenKbId = SpecimenId,
            SourceDisplayName = "Гемоглобин", RequestedByUserId = Guid.NewGuid(),
            Status = EnrichmentJobStatus.Failed, CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        await _sut.RequestAsync(
            "гемоглобин", SpecimenId, "Гемоглобин", null, Guid.NewGuid(),
            force: true, origin: EnrichmentRequestOrigin.SystemMaintenance);

        Db.LabAnalyteEnrichmentJobs.Should().HaveCount(2);
        Db.LabAnalyteEnrichmentJobs.Should().Contain(j => j.Status == EnrichmentJobStatus.Pending && j.Force);
    }

    [Fact]
    public async Task RequestAsync_UnresolvedSpecimen_NeverReachesFailedGate_StaysNoOp()
    {
        // Жёсткий гейт на нерезолвленный источник срабатывает ПЕРВЫМ — даже если по этому имени
        // уже была Failed-задача под другим (резолвленным) источником, это не должно влиять.
        await _sut.RequestAsync("гемоглобин", SpecimenContextIds.Unresolved, "Гемоглобин", null, Guid.NewGuid());

        Db.LabAnalyteEnrichmentJobs.Should().BeEmpty();
    }
}
