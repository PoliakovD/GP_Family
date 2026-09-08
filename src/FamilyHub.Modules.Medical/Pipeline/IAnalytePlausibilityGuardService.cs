namespace FamilyHub.Modules.Medical.Pipeline;

public record AnalytePlausibilityResult(bool IsPlausible, string? Reason)
{
    public static AnalytePlausibilityResult Plausible() => new(true, null);

    public static AnalytePlausibilityResult Implausible(string reason) => new(false, reason);
}

/// <summary>Гейт «на бред» для РУЧНОГО ввода показателя (см. class doc
/// AnalytePlausibilityGuardService) — вызывается ТОЛЬКО для
/// LabAnalyteEnrichmentJob.Origin == EnrichmentRequestOrigin.ManualEntry, отдельно от
/// ILegitimacyGuardService (тот проверяет prompt injection, не смысловую правдоподобность).</summary>
public interface IAnalytePlausibilityGuardService
{
    Task<AnalytePlausibilityResult> CheckAsync(string analyteName, string? specimenDisplayName, CancellationToken ct = default);
}
