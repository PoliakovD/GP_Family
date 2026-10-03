using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.TestUtils;
using FamilyHub.UnitTests.TestSupport;
using FluentAssertions;
using Hangfire;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>Кэш платного поиска, накопленный до появления единиц в запросе, получает Units одним LLM-вызовом
/// на строку; единица принимается только если буквально есть в тексте сниппетов.</summary>
public class LabAnalyteCacheUnitsBackfillJobTests : SqliteTestBase
{
    private readonly ILmStudioJsonClient _client = Substitute.For<ILmStudioJsonClient>();
    private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();

    private LabAnalyteCacheUnitsBackfillJob Job() => new(
        Db, _client, TestPromptProvider.ReturningFallback(), _jobs, NullLogger<LabAnalyteCacheUnitsBackfillJob>.Instance);

    private void AddCache(string name, string snippetText) => Db.LabAnalyteSearchCaches.Add(new LabAnalyteSearchCache
    {
        Id = Guid.NewGuid(), NormalizedName = name, SpecimenKbId = Guid.NewGuid(), SearchGroupKey = "group:" + name, Provider = "brave",
        LastUpdatedAt = DateTime.UtcNow, CanBeUpdatedAfter = DateTime.UtcNow.AddMonths(1),
        SnippetsJson = JsonSerializer.Serialize(new[] { new WebSnippet("t", "https://x.test", snippetText) }, SearchCacheSnippets.JsonOptions),
    });

    private void ModelReturns(params string[] units) =>
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(new LmStudioJsonResult(true, new Dictionary<string, JsonElement> { ["units"] = JsonSerializer.SerializeToElement(units) }, null));

    [Fact]
    public async Task RunAsync_StoresOnlyUnitsFoundInSnippetText()
    {
        AddCache("гемоглобин", "Норма гемоглобина 130-160 г/л или 8.1-9.9 ммоль/л");
        await Db.SaveChangesAsync();
        ModelReturns("г/л", "ммоль/л", "мкг/мл"); // последней в тексте нет — галлюцинация

        await Job().RunAsync();

        Db.LabAnalyteSearchCaches.Single().Units.Should().Be("г/л; ммоль/л");
    }

    [Fact]
    public async Task RunAsync_NoUnitsInSnippets_MarksRowCheckedWithEmptyString()
    {
        AddCache("белок", "общие сведения без цифр");
        await Db.SaveChangesAsync();
        ModelReturns();

        await Job().RunAsync();

        Db.LabAnalyteSearchCaches.Single().Units.Should().Be("");
    }

    [Fact]
    public async Task RunAsync_ModelUnavailable_KeepsRowUnchecked_AndThrowsForRetry()
    {
        AddCache("белок", "текст 5 г/л");
        await Db.SaveChangesAsync();
        _client.ExtractJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(LmStudioJsonResult.Failure("недоступен", isTransient: true));

        var act = () => Job().RunAsync();

        await act.Should().ThrowAsync<LmStudioUnavailableException>();
        Db.LabAnalyteSearchCaches.Single().Units.Should().BeNull();
    }

    [Theory]
    [InlineData("г/л; %", "г/л", true)]
    [InlineData("мг/дл", "mg/dL", true)]
    [InlineData("г/л", "%", false)]
    [InlineData("", "г/л", false)]
    [InlineData(null, "г/л", false)]
    public void CoversUnit_ComparesByCanonicalForm(string? cached, string wanted, bool expected) =>
        new CachedAnalyteSearch([], "brave", DateTime.UtcNow, DateTime.UtcNow, null, cached).CoversUnit(wanted).Should().Be(expected);
}
