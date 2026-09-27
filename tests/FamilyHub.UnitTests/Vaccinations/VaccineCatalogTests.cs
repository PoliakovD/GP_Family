using FamilyHub.Domain.Vaccinations;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Vaccinations;

public class VaccineCatalogTests
{
    [Fact]
    public void Codes_AreUnique()
    {
        VaccineCatalog.All.Select(s => s.Code).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EverySeries_HasAtLeastOneDoseOrIsPurelySeasonal()
    {
        VaccineCatalog.All.Should().OnlyContain(s => s.Doses.Count > 0);
    }

    [Fact]
    public void AgeBasedDoses_HaveFromNotAfterTo()
    {
        foreach (var series in VaccineCatalog.All)
        {
            foreach (var dose in series.Doses.Where(d => d.IsAgeBased))
            {
                var birth = new DateOnly(2000, 1, 1);
                var from = dose.AgeFrom!.Value.AddTo(birth);
                var to = dose.AgeTo!.Value.AddTo(birth);
                (from <= to).Should().BeTrue($"{series.Code}: {dose.Label} должен открываться не позже, чем закрывается");
            }
        }
    }

    [Fact]
    public void IntervalDoses_HaveFromNotAfterTo()
    {
        foreach (var series in VaccineCatalog.All)
        {
            foreach (var dose in series.Doses.Where(d => !d.IsAgeBased))
            {
                var anchor = new DateOnly(2020, 1, 1);
                var from = dose.IntervalFrom!.Value.AddTo(anchor);
                var to = dose.IntervalTo!.Value.AddTo(anchor);
                (from <= to).Should().BeTrue($"{series.Code}: {dose.Label} должен открываться не позже, чем закрывается");
            }
        }
    }

    [Fact]
    public void Find_UnknownCode_ReturnsNull()
    {
        VaccineCatalog.Find("does-not-exist").Should().BeNull();
    }

    [Fact]
    public void Find_KnownCode_ReturnsSeries()
    {
        VaccineCatalog.Find("mmr").Should().NotBeNull();
    }

    [Fact]
    public void SeasonalSeries_HaveExactlyOneDose()
    {
        foreach (var series in VaccineCatalog.All.Where(s => s.SeasonalWindow is not null))
            series.Doses.Should().HaveCount(1, $"{series.Code}: сезонная серия описывается одной дозой");
    }
}
