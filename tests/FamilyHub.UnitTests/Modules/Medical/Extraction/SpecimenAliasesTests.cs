using FamilyHub.Modules.Medical.Extraction;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical.Extraction;

/// <summary>Синонимы источника из админки нормализуются тем же ключом, по которому их ищет FindAsync.</summary>
public class SpecimenAliasesTests
{
    [Fact]
    public void NormalizeAliases_NormalizesDedupesAndDropsOwnNameAndEmpty()
    {
        var result = GlobalSpecimenKbService.NormalizeAliases(
            ["Венозная кровь", "венозная  КРОВЬ", " ", "Кровь", "Кровь (капиллярная)"], ownNormalizedName: "кровь");

        result.Should().Equal("венозная кровь");
    }
}
