namespace FamilyHub.Modules.Medical.Pipeline;

public record LegitimacyCheckResult(bool IsLegitimate, string? Reason)
{
    public static LegitimacyCheckResult Legitimate() => new(true, null);

    public static LegitimacyCheckResult Rejected(string reason) => new(false, reason);
}

/// <summary>Первый обязательный шаг каждого enrich/extraction-конвейера — см. class doc
/// LegitimacyGuardService для полного описания deny-by-default гарантии.</summary>
public interface ILegitimacyGuardService
{
    Task<LegitimacyCheckResult> CheckAsync(string text, CancellationToken ct = default);

    Task<LegitimacyCheckResult> CheckAsync(
        string text, IReadOnlyList<(byte[] Bytes, string ContentType)> images, CancellationToken ct = default);
}
