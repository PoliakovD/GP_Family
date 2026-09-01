using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Modules.Medical.Extraction;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>Детерминированный merge норм по приоритету источника (см. class doc ReferenceRangeMerger) —
/// чистая функция, без LLM/БД.</summary>
public class ReferenceRangeMergerTests
{
    private static readonly IReadOnlyList<string> Priority = ["invitro.ru", "gemotest.ru", "helix.ru"];

    private static readonly IReadOnlyList<WebSnippet> Snippets =
    [
        new WebSnippet("Invitro", "https://www.invitro.ru/analizes/for-doctors/analiz/glu/", "..."),
        new WebSnippet("Gemotest", "https://gemotest.ru/catalog/glu/", "..."),
        new WebSnippet("Helix", "https://helix.ru/kb/item/glu", "..."),
    ];

    [Fact]
    public void Merge_ConflictingRangesForSameGroup_HigherPriorityDomainWins()
    {
        var raw = new List<LabAnalyteReferenceRange>
        {
            new(AgeFrom: null, AgeTo: null, Sex: null, Low: 3.9, High: 6.1, Unit: "ммоль/л", SourceIndex: 1), // gemotest
            new(AgeFrom: null, AgeTo: null, Sex: null, Low: 4.1, High: 5.9, Unit: "ммоль/л", SourceIndex: 0), // invitro — приоритетнее
        };

        var merged = ReferenceRangeMerger.Merge(raw, Snippets, Priority);

        merged.Should().HaveCount(1);
        merged[0].Low.Should().Be(4.1);
        merged[0].High.Should().Be(5.9);
        merged[0].SourceDomain.Should().Be("www.invitro.ru");
        merged[0].SourceRank.Should().Be(0);
    }

    [Fact]
    public void Merge_GroupMissingFromTopSource_FilledFromLowerRankedSource()
    {
        // invitro даёт только общий диапазон, отдельную детскую группу знает только helix —
        // группа не должна теряться, даже если её нет у приоритетного источника.
        var raw = new List<LabAnalyteReferenceRange>
        {
            new(AgeFrom: null, AgeTo: null, Sex: null, Low: 4.1, High: 5.9, Unit: "ммоль/л", SourceIndex: 0), // invitro, general
            new(AgeFrom: 0, AgeTo: 14, Sex: null, Low: 3.3, High: 5.6, Unit: "ммоль/л",
                Population: LabPopulation.Children, SourceIndex: 2), // helix, children
        };

        var merged = ReferenceRangeMerger.Merge(raw, Snippets, Priority);

        merged.Should().HaveCount(2);
        merged.Should().Contain(r => r.AgeFrom == null && r.SourceDomain == "www.invitro.ru");
        merged.Should().Contain(r => r.AgeFrom == 0 && r.SourceDomain == "helix.ru");
    }

    [Fact]
    public void Merge_UnrecognizedDomain_RankedLowestAndLosesToTrustedDomain()
    {
        var unknownSnippets = new List<WebSnippet>
        {
            new("Unknown", "https://unknown-lab.example/glu", "..."),
            new("Gemotest", "https://gemotest.ru/catalog/glu/", "..."),
        };
        var raw = new List<LabAnalyteReferenceRange>
        {
            new(AgeFrom: null, AgeTo: null, Sex: null, Low: 1, High: 2, Unit: "ммоль/л", SourceIndex: 0), // unknown domain
            new(AgeFrom: null, AgeTo: null, Sex: null, Low: 3.9, High: 6.1, Unit: "ммоль/л", SourceIndex: 1), // gemotest, trusted
        };

        var merged = ReferenceRangeMerger.Merge(raw, unknownSnippets, Priority);

        merged.Should().HaveCount(1);
        merged[0].SourceDomain.Should().Be("gemotest.ru");
    }

    [Fact]
    public void Merge_DifferentSexAndAgeGroups_AllPreserved_NotCollapsed()
    {
        var raw = new List<LabAnalyteReferenceRange>
        {
            new(AgeFrom: null, AgeTo: null, Sex: Gender.Male, Low: 130, High: 160, Unit: "г/л", SourceIndex: 0),
            new(AgeFrom: null, AgeTo: null, Sex: Gender.Female, Low: 120, High: 150, Unit: "г/л", SourceIndex: 0),
        };

        var merged = ReferenceRangeMerger.Merge(raw, Snippets, Priority);

        merged.Should().HaveCount(2);
    }

    [Fact]
    public void Merge_EmptyInput_ReturnsEmpty()
    {
        ReferenceRangeMerger.Merge([], Snippets, Priority).Should().BeEmpty();
    }
}
