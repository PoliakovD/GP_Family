namespace FamilyHub.Modules.Medical.Pipeline;

/// <summary>См. <see cref="LegitimacyCheckResult.IsTransientFailure"/> — то же различие
/// "технически недоступно" vs "модель сознательно отклонила". Confidence/ConfidenceReason — см.
/// <see cref="LegitimacyCheckResult"/> (ADR-0018).</summary>
public record AnalytePlausibilityResult(
    bool IsPlausible, string? Reason, bool IsTransientFailure = false,
    double? Confidence = null, string? ConfidenceReason = null)
{
    public static AnalytePlausibilityResult Plausible(double? confidence = null, string? confidenceReason = null) =>
        new(true, null, false, confidence, confidenceReason);

    public static AnalytePlausibilityResult Implausible(string reason, bool isTransientFailure = false) => new(false, reason, isTransientFailure);
}

/// <summary>Гейт «на бред» для РУЧНОГО ввода показателя (см. class doc
/// AnalytePlausibilityGuardService) — вызывается ТОЛЬКО для
/// LabAnalyteEnrichmentJob.Origin == EnrichmentRequestOrigin.ManualEntry, отдельно от
/// ILegitimacyGuardService (тот проверяет prompt injection, не смысловую правдоподобность).</summary>
public interface IAnalytePlausibilityGuardService
{
    Task<AnalytePlausibilityResult> CheckAsync(string analyteName, string? specimenDisplayName, CancellationToken ct = default);
}
