using FamilyHub.Infrastructure.Enrichment;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Enrichment;

/// <summary>Решает, какие сниппеты реально доходят до суммаризатора (пересборка enrich-пайплайна) —
/// точечный override побеждает, иначе решает членство домена в текущем списке.</summary>
public class EnrichmentSnippetFilterTests
{
    private static readonly List<string> TrustedDomains = ["vidal.ru", "rlsnet.ru"];

    [Fact]
    public void SelectEnabled_NoOverrides_KeepsOnlyTrustedDomains()
    {
        var snippets = new List<WebSnippet>
        {
            new("Видаль", "https://www.vidal.ru/x", "..."),
            new("Спам", "https://spam.example.com/x", "..."),
        };

        var result = EnrichmentSnippetFilter.SelectEnabled(snippets, TrustedDomains, overrides: null);

        result.Should().ContainSingle();
        result[0].Url.Should().Be("https://www.vidal.ru/x");
    }

    [Fact]
    public void SelectEnabled_OverrideEnablesUntrustedDomain()
    {
        var snippets = new List<WebSnippet> { new("Спам", "https://spam.example.com/x", "...") };
        var overrides = new Dictionary<string, bool> { ["https://spam.example.com/x"] = true };

        var result = EnrichmentSnippetFilter.SelectEnabled(snippets, TrustedDomains, overrides);

        result.Should().ContainSingle("админ явно включил конкретный URL, несмотря на недоверенный домен");
    }

    [Fact]
    public void SelectEnabled_OverrideDisablesTrustedDomain()
    {
        var snippets = new List<WebSnippet> { new("Видаль", "https://vidal.ru/x", "...") };
        var overrides = new Dictionary<string, bool> { ["https://vidal.ru/x"] = false };

        var result = EnrichmentSnippetFilter.SelectEnabled(snippets, TrustedDomains, overrides);

        result.Should().BeEmpty("админ явно выключил конкретный URL, несмотря на доверенный домен");
    }

    [Fact]
    public void SelectEnabled_UnrelatedOverride_DoesNotAffectOtherSnippets()
    {
        var snippets = new List<WebSnippet>
        {
            new("Видаль", "https://vidal.ru/x", "..."),
            new("РЛС", "https://rlsnet.ru/y", "..."),
        };
        var overrides = new Dictionary<string, bool> { ["https://vidal.ru/x"] = false };

        var result = EnrichmentSnippetFilter.SelectEnabled(snippets, TrustedDomains, overrides);

        result.Should().ContainSingle();
        result[0].Url.Should().Be("https://rlsnet.ru/y");
    }

    [Theory]
    [InlineData("https://vidal.ru/x", true)]
    [InlineData("https://www.vidal.ru/x", true)] // поддомен доверенного домена
    [InlineData("https://notvidal.ru/x", false)]
    [InlineData("not-a-url", false)]
    public void IsTrustedDomain_ExactOrSubdomainMatch(string url, bool expected)
    {
        EnrichmentSnippetFilter.IsTrustedDomain(url, TrustedDomains).Should().Be(expected);
    }
}
