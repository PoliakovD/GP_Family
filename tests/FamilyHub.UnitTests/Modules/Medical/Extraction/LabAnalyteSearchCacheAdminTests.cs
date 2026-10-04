using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>Правка строки кэша поиска из админки: единицы, название для людей (не ключ) и поиск по нему.</summary>
public class LabAnalyteSearchCacheAdminTests : SqliteTestBase
{
    private LabAnalyteSearchCacheService Service() =>
        new(Db, Options.Create(new EnrichmentOptions()), NullLogger<LabAnalyteSearchCacheService>.Instance);

    private async Task<LabAnalyteSearchCache> AddCacheAsync(
        string name, string? displayName = null, string? units = null, string snippetsJson = "[{\"Title\":\"t\",\"Url\":\"https://x.test\",\"Text\":\"x\"}]")
    {
        var row = new LabAnalyteSearchCache
        {
            Id = Guid.NewGuid(), NormalizedName = name, DisplayName = displayName, SpecimenKbId = Guid.NewGuid(),
            SearchGroupKey = "group:" + name, Provider = "brave", LastUpdatedAt = DateTime.UtcNow,
            CanBeUpdatedAfter = DateTime.UtcNow.AddMonths(1), SnippetsJson = snippetsJson, Units = units,
        };
        Db.LabAnalyteSearchCaches.Add(row);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return row;
    }

    private LabAnalyteSearchCache Reload(Guid id)
    {
        Db.ChangeTracker.Clear();
        return Db.LabAnalyteSearchCaches.Single(c => c.Id == id);
    }

    [Theory]
    [InlineData("г/л, ммоль/л;Г/л; ", "г/л; ммоль/л")]
    [InlineData("  ", "")]
    [InlineData("%", "%")]
    public void Units_Normalize_SplitsTrimsAndDedupes(string raw, string expected)
    {
        var (value, error) = SearchCacheUnits.Normalize(raw);

        error.Should().BeNull();
        value.Should().Be(expected);
    }

    [Fact]
    public void Units_Normalize_TooLong_ReturnsError()
    {
        var raw = string.Join(";", Enumerable.Range(0, 60).Select(i => $"ед{i}"));

        SearchCacheUnits.Normalize(raw).Error.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateAsync_Units_SetKeepAndReset()
    {
        var row = await AddCacheAsync("гемоглобин", units: null);
        var snippets = SearchCacheSnippets.Parse(row.SnippetsJson);

        await Service().UpdateAsync(row.Id, null, snippets, units: "г/л; ммоль/л");
        Reload(row.Id).Units.Should().Be("г/л; ммоль/л");

        await Service().UpdateAsync(row.Id, null, snippets); // units не передан — не трогаем
        Reload(row.Id).Units.Should().Be("г/л; ммоль/л");

        await Service().UpdateAsync(row.Id, null, snippets, units: "");
        Reload(row.Id).Units.Should().Be("", "пустой список — «проверено, единиц нет»");

        await Service().UpdateAsync(row.Id, null, snippets, resetUnits: true);
        Reload(row.Id).Units.Should().BeNull("сброс возвращает строку в «не определено» для фоновой разметки");
    }

    [Fact]
    public async Task UpdateAsync_DisplayName_EditsWithoutTouchingKey()
    {
        var row = await AddCacheAsync("срб", displayName: null);
        var snippets = SearchCacheSnippets.Parse(row.SnippetsJson);

        await Service().UpdateAsync(row.Id, null, snippets, displayName: "  СРБ  ");
        var updated = Reload(row.Id);
        updated.DisplayName.Should().Be("СРБ");
        updated.NormalizedName.Should().Be("срб");

        await Service().UpdateAsync(row.Id, null, snippets, displayName: "");
        Reload(row.Id).DisplayName.Should().BeNull();
    }

    [Fact]
    public async Task RecordSearchAsync_SetsDisplayNameOnce_AdminEditSurvivesNextSearch()
    {
        var specimen = Guid.NewGuid();
        var snippets = new[] { new WebSnippet("t", "https://x.test", "x") };

        await Service().RecordSearchAsync("ачтв", specimen, "brave", snippets, displayName: "АЧТВ");
        Db.ChangeTracker.Clear();
        var row = Db.LabAnalyteSearchCaches.Single();
        row.DisplayName.Should().Be("АЧТВ");

        await Service().UpdateAsync(row.Id, null, snippets, displayName: "АЧТВ (активированное частичное тромбопластиновое время)");
        await Service().RecordSearchAsync("ачтв", specimen, "brave", snippets, displayName: "ачтв из другого бланка");

        Reload(row.Id).DisplayName.Should().Be("АЧТВ (активированное частичное тромбопластиновое время)");
    }

    [Fact]
    public async Task ListAsync_FindsByDisplayNameAndByFoldedKey()
    {
        await AddCacheAsync("гемоглобин", displayName: "Гемоглобин (HGB)");
        await AddCacheAsync("аденовирус", displayName: null);
        await AddCacheAsync("ферритин", displayName: "Ферритин");

        var (byDisplay, _) = await Service().ListAsync("HGB", 0, 10);
        byDisplay.Select(r => r.NormalizedName).Should().Equal("гемоглобин");

        var (byFoldedKey, _) = await Service().ListAsync("Adenovirus", 0, 10);
        byFoldedKey.Select(r => r.NormalizedName).Should().Equal("аденовирус");
    }

    [Fact]
    public async Task ListAsync_UnitsUndetermined_OnlyRowsWithSnippetsAndNullUnits()
    {
        await AddCacheAsync("гемоглобин", units: null);
        await AddCacheAsync("ферритин", units: "мкг/л");
        await AddCacheAsync("белок", units: "");
        await AddCacheAsync("пусто", units: null, snippetsJson: "[]");

        var (rows, total) = await Service().ListAsync(null, 0, 10, unitsUndetermined: true);

        total.Should().Be(1);
        rows.Single().NormalizedName.Should().Be("гемоглобин");
    }
}
