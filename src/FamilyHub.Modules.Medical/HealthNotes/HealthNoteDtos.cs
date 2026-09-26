using FamilyHub.Domain.Enums;
using FamilyHub.Domain.HealthNotes;

namespace FamilyHub.Modules.Medical.HealthNotes;

/// <summary>Запись дневника наружу. Ровно один из payload'ов (Symptom…Sleep) соответствует Kind,
/// остальные null; у Note payload'а нет.</summary>
public record HealthNoteDto(
    Guid Id,
    HealthNoteKind Kind,
    DateTime OccurredAt,
    string? Title,
    string? Text,
    bool IncludeInDoctorQuestions,
    SymptomData? Symptom,
    MetricData? Metric,
    WellbeingData? Wellbeing,
    MedicationIntakeData? Intake,
    SleepData? Sleep,
    DateTime UpdatedAt);

/// <summary>Тело POST/PUT — та же форма, что и у DTO, без служебных полей.</summary>
public record HealthNoteRequest(
    HealthNoteKind Kind,
    DateTime OccurredAt,
    string? Title,
    string? Text,
    bool IncludeInDoctorQuestions,
    SymptomData? Symptom,
    MetricData? Metric,
    WellbeingData? Wellbeing,
    MedicationIntakeData? Intake,
    SleepData? Sleep);

/// <summary>Точка ряда одного замера для графика.</summary>
public record HealthMetricPoint(DateTime OccurredAt, decimal Value, decimal? Value2);

public record HealthNoteCatalogResponse(
    IReadOnlyList<HealthMetricDefinition> Metrics,
    IReadOnlyCollection<string> BodyAreas,
    IReadOnlyCollection<string> WellbeingFactors);

public enum HealthNoteResult { Success, NotFound, Invalid }
