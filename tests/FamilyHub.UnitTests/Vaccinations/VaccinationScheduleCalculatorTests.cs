using FamilyHub.Domain.Vaccinations;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Vaccinations;

public class VaccinationScheduleCalculatorTests
{
    private static VaccinationFact Done(string series, int doseIndex, DateOnly date) =>
        new(Guid.NewGuid(), series, doseIndex, VaccinationKind.Done, date, VaccinationDatePrecision.Day);

    private static VaccinationFact Unknown(string series, int doseIndex) =>
        new(Guid.NewGuid(), series, doseIndex, VaccinationKind.Unknown, null, null);

    private static VaccinationFact HadDisease(string series, DateOnly? date = null) =>
        new(Guid.NewGuid(), series, null, VaccinationKind.HadDisease, date, date is null ? null : VaccinationDatePrecision.Year);

    private static ScheduleItem Item(IReadOnlyList<ScheduleItem> items, string series, int doseIndex) =>
        items.Single(i => i.SeriesCode == series && i.DoseIndex == doseIndex);

    [Fact]
    public void ChildJustTurnedSix_MmrBooster_IsDueSoon()
    {
        var birth = new DateOnly(2020, 11, 12);
        var today = new DateOnly(2026, 11, 20); // 6 лет и 8 дней
        var facts = new List<VaccinationFact> { Done("mmr", 0, birth.AddMonths(12)) };

        var items = VaccinationScheduleCalculator.Calculate(birth, facts, today);

        Item(items, "mmr", 1).Status.Should().Be(VaccinationStatus.DueSoon);
    }

    [Fact]
    public void ChildFiveYearsOld_MmrBooster_IsUpcoming()
    {
        var birth = new DateOnly(2021, 11, 12);
        var today = new DateOnly(2026, 11, 20); // 5 лет
        var facts = new List<VaccinationFact> { Done("mmr", 0, birth.AddMonths(12)) };

        var items = VaccinationScheduleCalculator.Calculate(birth, facts, today);

        Item(items, "mmr", 1).Status.Should().Be(VaccinationStatus.Upcoming);
    }

    [Fact]
    public void AdultNoChildhoodData_BcgBooster_IsNoData()
    {
        var birth = new DateOnly(1962, 1, 1); // 64 года
        var today = new DateOnly(2026, 1, 1);

        var items = VaccinationScheduleCalculator.Calculate(birth, [], today);

        Item(items, "bcg", 1).Status.Should().Be(VaccinationStatus.NoData);
    }

    [Fact]
    public void UnknownAnswer_GivesNoDataAndKeepsRecordId()
    {
        var birth = new DateOnly(1962, 1, 1);
        var today = new DateOnly(2026, 1, 1);
        var fact = Unknown("bcg", 1);

        var items = VaccinationScheduleCalculator.Calculate(birth, [fact], today);

        var item = Item(items, "bcg", 1);
        item.Status.Should().Be(VaccinationStatus.NoData);
        item.RecordId.Should().Be(fact.RecordId);
    }

    [Fact]
    public void MeaslesRubellaMumps_HadDisease_ClosesWholeSeriesAsOneItem()
    {
        var birth = new DateOnly(2020, 1, 1);
        var today = new DateOnly(2026, 1, 1);
        var disease = HadDisease("mmr", new DateOnly(2022, 3, 1));

        var items = VaccinationScheduleCalculator.Calculate(birth, [disease], today);

        var mmrItems = items.Where(i => i.SeriesCode == "mmr").ToList();
        mmrItems.Should().ContainSingle();
        mmrItems[0].Status.Should().Be(VaccinationStatus.HadDisease);
        mmrItems[0].DoseIndex.Should().Be(-1);
    }

    [Fact]
    public void AdultDtpBooster_JustOverdue_IsCanDo()
    {
        var birth = new DateOnly(1998, 1, 1);
        var last = birth.AddYears(14); // Ревакцинация 3 (АДС-М) в 14 лет
        var today = last.AddYears(10).AddDays(200); // просрочен на 200 дней — в пределах года
        var facts = new List<VaccinationFact> { Done("dtp", 5, last) };

        var items = VaccinationScheduleCalculator.Calculate(birth, facts, today);

        var repeat = Item(items, "dtp", 6);
        repeat.Status.Should().Be(VaccinationStatus.CanDo);
        repeat.SeriesName.Should().Be("Дифтерия, столбняк (АДС-М)");
    }

    [Fact]
    public void AdultDtpBooster_OverdueMoreThanYear_IsNoData()
    {
        var birth = new DateOnly(1990, 1, 1);
        var last = birth.AddYears(14);
        var today = last.AddYears(10).AddDays(400);
        var facts = new List<VaccinationFact> { Done("dtp", 5, last) };

        var items = VaccinationScheduleCalculator.Calculate(birth, facts, today);

        Item(items, "dtp", 6).Status.Should().Be(VaccinationStatus.NoData);
    }

    [Fact]
    public void AdultDtpBooster_DoneTwice_AdvancesToNextRepeat()
    {
        var birth = new DateOnly(1980, 1, 1);
        var first = birth.AddYears(14);
        var second = first.AddYears(10);
        var today = second.AddYears(9);
        var facts = new List<VaccinationFact> { Done("dtp", 5, first), Done("dtp", 6, second) };

        var items = VaccinationScheduleCalculator.Calculate(birth, facts, today);

        items.Should().Contain(i => i.SeriesCode == "dtp" && i.DoseIndex == 6 && i.Status == VaccinationStatus.Done);
        // today — за год до срока третьего повтора (second + 10 лет): ещё рано, не в окне «скоро».
        Item(items, "dtp", 7).Status.Should().Be(VaccinationStatus.Upcoming);
    }

    [Fact]
    public void Flu_NoRecord_DuringSeason_IsDueSoon()
    {
        var birth = new DateOnly(2000, 1, 1);
        var today = new DateOnly(2026, 10, 15); // октябрь — сезон

        var items = VaccinationScheduleCalculator.Calculate(birth, [], today);

        Item(items, "flu", 0).Status.Should().Be(VaccinationStatus.DueSoon);
    }

    [Fact]
    public void Flu_NoRecord_OutOfSeason_IsCanDo()
    {
        var birth = new DateOnly(2000, 1, 1);
        var today = new DateOnly(2026, 3, 1); // март — вне сезона, давно прошёл

        var items = VaccinationScheduleCalculator.Calculate(birth, [], today);

        Item(items, "flu", 0).Status.Should().Be(VaccinationStatus.CanDo);
    }

    [Fact]
    public void Flu_DoneThisSeason_IsDone()
    {
        var birth = new DateOnly(2000, 1, 1);
        var today = new DateOnly(2026, 11, 1);
        var facts = new List<VaccinationFact> { Done("flu", 0, new DateOnly(2026, 10, 3)) };

        var items = VaccinationScheduleCalculator.Calculate(birth, facts, today);

        Item(items, "flu", 0).Status.Should().Be(VaccinationStatus.Done);
    }

    [Fact]
    public void Flu_TooYoung_DoesNotAppear()
    {
        var birth = new DateOnly(2026, 9, 1);
        var today = new DateOnly(2026, 10, 15);

        var items = VaccinationScheduleCalculator.Calculate(birth, [], today);

        items.Should().NotContain(i => i.SeriesCode == "flu");
    }

    [Fact]
    public void EpidemicSeries_NoRecords_DoesNotAppearAtAll()
    {
        var birth = new DateOnly(1990, 1, 1);
        var today = new DateOnly(2026, 1, 1);

        var items = VaccinationScheduleCalculator.Calculate(birth, [], today);

        items.Should().NotContain(i => i.SeriesCode == "tick_borne_encephalitis");
    }

    [Fact]
    public void EpidemicSeries_AfterFirstDose_SecondDoseWindowAnchoredToFirstDoseDate()
    {
        var birth = new DateOnly(1990, 1, 1);
        var firstDose = new DateOnly(2026, 3, 12);
        var today = new DateOnly(2026, 4, 20); // окно второй дозы уже открылось (через месяц после первой)
        var facts = new List<VaccinationFact> { Done("tick_borne_encephalitis", 0, firstDose) };

        var items = VaccinationScheduleCalculator.Calculate(birth, facts, today);

        var second = Item(items, "tick_borne_encephalitis", 1);
        second.WindowFrom.Should().Be(firstDose.AddMonths(1));
        second.WindowTo.Should().Be(firstDose.AddMonths(3));
        second.Status.Should().Be(VaccinationStatus.DueSoon);
    }

    [Fact]
    public void EpidemicSeries_SecondDoseNotYetDone_ThirdDoseDoesNotAppear()
    {
        var birth = new DateOnly(1990, 1, 1);
        var facts = new List<VaccinationFact> { Done("tick_borne_encephalitis", 0, new DateOnly(2026, 3, 12)) };
        var today = new DateOnly(2026, 4, 1);

        var items = VaccinationScheduleCalculator.Calculate(birth, facts, today);

        items.Should().NotContain(i => i.SeriesCode == "tick_borne_encephalitis" && i.DoseIndex == 2);
    }

    [Fact]
    public void Calculate_IsDeterministic_ForSameInputs()
    {
        var birth = new DateOnly(2019, 6, 15);
        var today = new DateOnly(2026, 9, 27);
        var facts = new List<VaccinationFact> { Done("bcg", 0, birth.AddDays(4)), Unknown("mmr", 0) };

        var first = VaccinationScheduleCalculator.Calculate(birth, facts, today);
        var second = VaccinationScheduleCalculator.Calculate(birth, facts, today);

        first.Should().BeEquivalentTo(second);
    }
}
