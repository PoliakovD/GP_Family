using FamilyHub.Domain.Enums;

namespace FamilyHub.Modules.Medical.Extraction;

public record ExtractionStatusResponse(EnrichmentJobStatus Status, ExtractionStage Stage, int IndicatorCount, string? Error, DateTime CreatedAt, DateTime? CompletedAt);

public record IndicatorDto(
    Guid Id, string AnalyteKey, string DisplayName, IndicatorFlag Flag, int Position,
    string ValueRaw, string? Unit, string? RefLowText, string? RefHighText, string? RefText,
    DateOnly RecordDate, Guid MedicalRecordId);

public record IndicatorHistoryPoint(DateOnly RecordDate, string ValueRaw, string? ValueNumericText, IndicatorFlag Flag, Guid MedicalRecordId);

public record MyIndicatorSummary(string AnalyteKey, string DisplayName, string ValueRaw, string? Unit, IndicatorFlag Flag, DateOnly LastRecordDate);

/// <summary>Форма MedicalRecord.SummaryJson, которую пишет LabSummarizer — используется только
/// для десериализации на чтении.</summary>
public record RecordSummaryResponse(string? PlainSummary, IReadOnlyList<LabSummaryDeviation> Deviations, IReadOnlyList<string> QuestionsForDoctor, string Disclaimer);
