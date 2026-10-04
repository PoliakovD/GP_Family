using FamilyHub.Api.Features.Admin;
using FamilyHub.Infrastructure.Enrichment;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Features.Admin;

/// <summary>Сохранение в «Кэш поиска» не должно превращать ручные/закреплённые сниппеты из «Одобрения» в обычные авто.</summary>
public class SearchCacheEditSnippetsTests
{
    private static readonly WebSnippet StoredManual =
        new("Норма", "https://lab.test/a", "130-160 г/л", SnippetOrigin.Manual, "manual-quote", "сверено", Pinned: true);

    [Fact]
    public void ToSnippets_ClientWithoutMetadata_KeepsStoredMetadataByUrl()
    {
        var input = new List<SearchCacheSnippetInput> { new("Норма (правка)", "https://LAB.test/a", "130-160 г/л") };

        var result = AdminEnrichmentEndpoints.ToSnippets(input, [StoredManual]);

        result.Should().ContainSingle().Which.Should().Be(new WebSnippet(
            "Норма (правка)", "https://LAB.test/a", "130-160 г/л", SnippetOrigin.Manual, "manual-quote", "сверено", Pinned: true));
    }

    [Fact]
    public void ToSnippets_ClientSendsMetadata_ItWins()
    {
        var input = new List<SearchCacheSnippetInput>
        {
            new("Норма", "https://lab.test/a", "x", SnippetOrigin.Auto, null, null, false),
        };

        var result = AdminEnrichmentEndpoints.ToSnippets(input, [StoredManual]);

        result.Single().Origin.Should().Be(SnippetOrigin.Auto);
        result.Single().Pinned.Should().BeFalse();
    }

    [Fact]
    public void ToSnippets_NewUrl_IsAuto()
    {
        var input = new List<SearchCacheSnippetInput> { new("Новый", "https://new.test", "x") };

        AdminEnrichmentEndpoints.ToSnippets(input, [StoredManual]).Single().Origin.Should().Be(SnippetOrigin.Auto);
    }
}
