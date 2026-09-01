using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.Enrichment;
using FamilyHub.TestUtils;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Enrichment;

/// <summary>Доверенные домены — БД-backed, управляются через админку (пересборка enrich-пайплайна) —
/// см. class doc EnrichmentTrustedDomainService.</summary>
public class EnrichmentTrustedDomainServiceTests : SqliteTestBase
{
    private readonly EnrichmentTrustedDomainService _sut;

    public EnrichmentTrustedDomainServiceTests()
    {
        _sut = new EnrichmentTrustedDomainService(Db);
    }

    [Fact]
    public async Task AddAsync_NormalizesFullUrlToHost_StripsWww()
    {
        var (success, domain) = await _sut.AddAsync(WebSearchTopic.Medication, "https://www.vidal.ru/some/path");

        success.Should().BeTrue();
        domain!.Domain.Should().Be("vidal.ru");
    }

    [Fact]
    public async Task AddAsync_AssignsIncrementingRank_PerTopic()
    {
        await _sut.AddAsync(WebSearchTopic.Medication, "vidal.ru");
        await _sut.AddAsync(WebSearchTopic.Medication, "rlsnet.ru");
        // Другая тема — ранг начинается заново, темы не делят один счётчик.
        await _sut.AddAsync(WebSearchTopic.LabAnalyte, "invitro.ru");

        var medication = await _sut.GetAllAsync(WebSearchTopic.Medication);
        var labAnalyte = await _sut.GetAllAsync(WebSearchTopic.LabAnalyte);

        medication.Select(d => d.Rank).Should().Equal(0, 1);
        labAnalyte.Select(d => d.Rank).Should().Equal(0);
    }

    [Fact]
    public async Task AddAsync_DuplicateDomainSameTopic_Fails()
    {
        await _sut.AddAsync(WebSearchTopic.Medication, "vidal.ru");

        var (success, domain) = await _sut.AddAsync(WebSearchTopic.Medication, "vidal.ru");

        success.Should().BeFalse();
        domain.Should().BeNull();
    }

    [Fact]
    public async Task AddAsync_SameDomainDifferentTopic_BothSucceed()
    {
        var (successA, _) = await _sut.AddAsync(WebSearchTopic.Medication, "example-lab.ru");
        var (successB, _) = await _sut.AddAsync(WebSearchTopic.LabAnalyte, "example-lab.ru");

        successA.Should().BeTrue();
        successB.Should().BeTrue();
    }

    [Fact]
    public async Task GetActiveDomainsByPriorityAsync_ExcludesDisabled_OrdersByRank()
    {
        var (_, first) = await _sut.AddAsync(WebSearchTopic.LabAnalyte, "gemotest.ru");
        await _sut.AddAsync(WebSearchTopic.LabAnalyte, "invitro.ru");
        await _sut.SetEnabledAsync(first!.Id, false);

        var active = await _sut.GetActiveDomainsByPriorityAsync(WebSearchTopic.LabAnalyte);

        active.Should().Equal("invitro.ru");
    }

    [Fact]
    public async Task DeleteAsync_RemovesDomain_UnknownIdReturnsFalse()
    {
        var (_, domain) = await _sut.AddAsync(WebSearchTopic.Medication, "vidal.ru");

        var deleted = await _sut.DeleteAsync(domain!.Id);
        var deletedAgain = await _sut.DeleteAsync(domain.Id);
        var deletedUnknown = await _sut.DeleteAsync(Guid.NewGuid());

        deleted.Should().BeTrue();
        deletedAgain.Should().BeFalse();
        deletedUnknown.Should().BeFalse();
        (await _sut.GetAllAsync(WebSearchTopic.Medication)).Should().BeEmpty();
    }

    [Fact]
    public async Task SetOrderAsync_ReordersByGivenIdSequence()
    {
        var (_, a) = await _sut.AddAsync(WebSearchTopic.LabAnalyte, "invitro.ru");
        var (_, b) = await _sut.AddAsync(WebSearchTopic.LabAnalyte, "gemotest.ru");
        var (_, c) = await _sut.AddAsync(WebSearchTopic.LabAnalyte, "helix.ru");

        // Новый порядок: helix, invitro, gemotest.
        await _sut.SetOrderAsync(WebSearchTopic.LabAnalyte, [c!.Id, a!.Id, b!.Id]);

        var active = await _sut.GetActiveDomainsByPriorityAsync(WebSearchTopic.LabAnalyte);
        active.Should().Equal("helix.ru", "invitro.ru", "gemotest.ru");
    }
}
