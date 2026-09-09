namespace FamilyHub.Modules.Medical.Pipeline;

/// <summary>См. <see cref="LegitimacyCheckResult.IsTransientFailure"/> — то же различие
/// "технически недоступно" vs "модель сознательно отклонила".</summary>
public record AnalytePlausibilityResult(bool IsPlausible, string? Reason, bool IsTransientFailure = false)
{
    public static AnalytePlausibilityResult Plausible() => new(true, null);

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
