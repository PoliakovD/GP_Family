using FamilyHub.Api.Features.Admin;
using FamilyHub.Domain.Entities;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FamilyHub.UnitTests.Features.Admin;

/// <summary>Прогресс пакетных операций «Пересборок» считается по данным; без хранилища Hangfire — Active=false, не падение.</summary>
public class AdminBatchStatusServiceTests : SqliteTestBase
{
    private void AddCache(string name, string? units, string snippets = "[{\"Title\":\"t\",\"Url\":\"https://x.test\",\"Text\":\"x\"}]") =>
        Db.LabAnalyteSearchCaches.Add(new LabAnalyteSearchCache
        {
            Id = Guid.NewGuid(), NormalizedName = name, SpecimenKbId = Guid.NewGuid(), SearchGroupKey = "g:" + name,
            Provider = "brave", LastUpdatedAt = DateTime.UtcNow, CanBeUpdatedAfter = DateTime.UtcNow, SnippetsJson = snippets, Units = units,
        });

    private void AddKb(string name, int version) => Db.GlobalLabAnalytesKb.Add(new GlobalLabAnalyteKb
    {
        Id = Guid.NewGuid(), NormalizedName = name, DisplayName = name, PayloadJson = "{}", Source = "test",
        PayloadVersion = version, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    });

    [Fact]
    public async Task GetAsync_CountsRemainingByData()
    {
        AddCache("a", null);
        AddCache("b", "г/л");
        AddCache("c", null, "[]"); // без выдачи — не считается
        AddKb("x", LabAnalyteSummarySchema.CurrentVersion - 1);
        AddKb("y", LabAnalyteSummarySchema.CurrentVersion);
        await Db.SaveChangesAsync();

        var status = await new AdminBatchStatusService(Db, NullLogger<AdminBatchStatusService>.Instance).GetAsync();

        var units = status.Jobs.Single(j => j.Key == AdminBatchStatusService.CacheUnits);
        units.Remaining.Should().Be(1);
        units.Total.Should().Be(2);
        units.Active.Should().BeFalse();

        var reenrich = status.Jobs.Single(j => j.Key == AdminBatchStatusService.Reenrich);
        reenrich.Remaining.Should().Be(1);
        reenrich.Total.Should().Be(2);
    }
}
