using FamilyHub.Domain.Enums;
using FamilyHub.Domain.HealthNotes;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.HealthNotes;

public class HealthNoteRulesTests
{
    private static readonly DateTime Bed = new(2026, 9, 25, 20, 40, 0, DateTimeKind.Utc);

    [Fact]
    public void Symptom_Valid_Passes()
    {
        var c = new HealthNoteContent(HealthNoteKind.Symptom, "Головная боль", null,
            Symptom: new SymptomData(7, ["head"], "затылок"));

        HealthNoteRules.Validate(c).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Symptom_SeverityOutOfRange_Rejected(int severity)
    {
        var c = new HealthNoteContent(HealthNoteKind.Symptom, "Боль", null, Symptom: new SymptomData(severity, null, null));

        HealthNoteRules.Validate(c).Should().NotBeNull();
    }

    [Fact]
    public void Symptom_WithoutTitle_Rejected() =>
        HealthNoteRules.Validate(new HealthNoteContent(HealthNoteKind.Symptom, " ", null,
            Symptom: new SymptomData(5, null, null))).Should().NotBeNull();

    [Fact]
    public void Symptom_UnknownArea_Rejected() =>
        HealthNoteRules.Validate(new HealthNoteContent(HealthNoteKind.Symptom, "Боль", null,
            Symptom: new SymptomData(5, ["elbow"], null))).Should().NotBeNull();

    [Fact]
    public void BloodPressure_RequiresSecondValue_LowerThanFirst()
    {
        Validate(new MetricData("blood_pressure", 128, 84)).Should().BeNull();
        Validate(new MetricData("blood_pressure", 128, null)).Should().NotBeNull();
        Validate(new MetricData("blood_pressure", 84, 128)).Should().NotBeNull("нижнее не может быть выше верхнего");
    }

    [Fact]
    public void SingleValueMetric_RejectsSecondValue_AndOutOfRange()
    {
        Validate(new MetricData("pulse", 72, null)).Should().BeNull();
        Validate(new MetricData("pulse", 72, 60)).Should().NotBeNull();
        Validate(new MetricData("pulse", 720, null)).Should().NotBeNull("опечатка вне правдоподобного диапазона");
        Validate(new MetricData("nonsense", 1, null)).Should().NotBeNull();
    }

    [Fact]
    public void Wellbeing_ScoreAndFactors_Validated()
    {
        HealthNoteRules.Validate(new HealthNoteContent(HealthNoteKind.Wellbeing, null, null,
            Wellbeing: new WellbeingData(4, ["rested"]))).Should().BeNull();
        HealthNoteRules.Validate(new HealthNoteContent(HealthNoteKind.Wellbeing, null, null,
            Wellbeing: new WellbeingData(6, null))).Should().NotBeNull();
        HealthNoteRules.Validate(new HealthNoteContent(HealthNoteKind.Wellbeing, null, null,
            Wellbeing: new WellbeingData(3, ["lunar-phase"]))).Should().NotBeNull();
    }

    [Fact]
    public void Sleep_WakeMustFollowBed_AndDurationIsDerived()
    {
        var ok = new SleepData(Bed, Bed.AddMinutes(440), 3);
        HealthNoteRules.SleepMinutes(ok).Should().Be(440);
        HealthNoteRules.Validate(new HealthNoteContent(HealthNoteKind.Sleep, null, null, Sleep: ok)).Should().BeNull();

        HealthNoteRules.Validate(new HealthNoteContent(HealthNoteKind.Sleep, null, null,
            Sleep: new SleepData(Bed, Bed.AddMinutes(-5), 2))).Should().NotBeNull();
        HealthNoteRules.Validate(new HealthNoteContent(HealthNoteKind.Sleep, null, null,
            Sleep: new SleepData(Bed, Bed.AddHours(25), 2))).Should().NotBeNull();
        HealthNoteRules.Validate(new HealthNoteContent(HealthNoteKind.Sleep, null, null,
            Sleep: new SleepData(Bed, Bed.AddHours(7), 4))).Should().NotBeNull();
    }

    [Fact]
    public void MedicationIntake_NeedsName_DoseOptional()
    {
        HealthNoteRules.Validate(new HealthNoteContent(HealthNoteKind.MedicationIntake, "Сорбифер", null)).Should().BeNull();
        HealthNoteRules.Validate(new HealthNoteContent(HealthNoteKind.MedicationIntake, null, null)).Should().NotBeNull();
    }

    [Fact]
    public void Note_NeedsText_AndForbidsPayload()
    {
        HealthNoteRules.Validate(new HealthNoteContent(HealthNoteKind.Note, null, "Спросить у терапевта")).Should().BeNull();
        HealthNoteRules.Validate(new HealthNoteContent(HealthNoteKind.Note, null, "  ")).Should().NotBeNull();
        HealthNoteRules.Validate(new HealthNoteContent(HealthNoteKind.Note, null, "текст",
            Intake: new MedicationIntakeData("1 таб."))).Should().NotBeNull();
    }

    [Fact]
    public void PayloadOfWrongKind_Rejected() =>
        HealthNoteRules.Validate(new HealthNoteContent(HealthNoteKind.Symptom, "Боль", null,
            Symptom: new SymptomData(5, null, null), Metric: new MetricData("pulse", 70, null))).Should().NotBeNull();

    [Fact]
    public void SerializeThenParse_RoundTripsPayload()
    {
        var original = new HealthNoteContent(HealthNoteKind.Metric, null, null, Metric: new MetricData("blood_pressure", 128, 84));

        var json = HealthNoteRules.SerializePayload(original);
        var parsed = HealthNoteRules.ParseContent(HealthNoteKind.Metric, null, null, json);

        parsed.Metric.Should().Be(original.Metric);
    }

    [Fact]
    public void ParseContent_CorruptJson_DoesNotThrow_AndKeepsTextFields()
    {
        var parsed = HealthNoteRules.ParseContent(HealthNoteKind.Symptom, "Боль", "текст", "{not json");

        parsed.Symptom.Should().BeNull();
        parsed.Title.Should().Be("Боль");
        parsed.Text.Should().Be("текст");
    }

    private static string? Validate(MetricData metric) =>
        HealthNoteRules.Validate(new HealthNoteContent(HealthNoteKind.Metric, null, null, Metric: metric));
}
