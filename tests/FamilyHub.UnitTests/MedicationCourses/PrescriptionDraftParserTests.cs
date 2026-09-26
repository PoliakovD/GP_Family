using FamilyHub.Domain.Enums;
using FamilyHub.Domain.MedicationCourses;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.MedicationCourses;

public class PrescriptionDraftParserTests
{
    [Fact]
    public void FullSentence_ParsedIntoDraft()
    {
        var d = PrescriptionDraftParser.Parse("По 1 таблетке 2 раза в день после еды, 8 недель");

        d.Mode.Should().Be(DoseScheduleMode.TimesPerDay);
        d.TimesPerDay.Should().Be(2);
        d.Units.Should().Be(1);
        d.Unit.Should().Be(DoseUnit.Tablet);
        d.Food.Should().Be(FoodRelation.After);
        d.DurationDays.Should().Be(56);
    }

    [Fact]
    public void EveryNHours_Recognised()
    {
        var d = PrescriptionDraftParser.Parse("1 таб. каждые 12 часов во время еды, 7 дней");

        d.Mode.Should().Be(DoseScheduleMode.EveryNHours);
        d.IntervalHours.Should().Be(12);
        d.Food.Should().Be(FoodRelation.With);
        d.DurationDays.Should().Be(7);
    }

    [Fact]
    public void UnsupportedInterval_NotGuessed() =>
        // 5 часов не делит сутки — режим не выбираем, пусть пользователь решит.
        PrescriptionDraftParser.Parse("каждые 5 часов").Mode.Should().BeNull();

    [Theory]
    [InlineData("при боли по 1 таблетке, не более 3 раз в сутки")]
    [InlineData("по необходимости")]
    public void AsNeeded_Recognised(string text) =>
        PrescriptionDraftParser.Parse(text).Mode.Should().Be(DoseScheduleMode.AsNeeded);

    [Theory]
    [InlineData("2 р/д", 2)]
    [InlineData("три раза в день", 3)]
    [InlineData("дважды в день", 2)]
    [InlineData("утром и вечером", 2)]
    [InlineData("1 раз в сутки", 1)]
    public void TimesPerDay_Variants(string text, int expected) =>
        PrescriptionDraftParser.Parse(text).TimesPerDay.Should().Be(expected);

    [Fact]
    public void FractionOfTablet() => PrescriptionDraftParser.Parse("1/2 таблетки утром").Units.Should().Be(0.5m);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("принимать согласно инструкции")]
    public void Unrecognised_GivesEmptyDraft(string? text)
    {
        var d = PrescriptionDraftParser.Parse(text);

        d.Mode.Should().BeNull();
        d.Units.Should().BeNull();
        d.Food.Should().BeNull();
        d.DurationDays.Should().BeNull();
    }
}
