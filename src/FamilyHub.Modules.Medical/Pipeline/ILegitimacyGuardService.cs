namespace FamilyHub.Modules.Medical.Pipeline;

/// <summary><see cref="IsTransientFailure"/> — отказ вызван технической недоступностью LM Studio
/// (см. LmStudioJsonResult.IsTransient), а не смысловым решением модели — вызывающий процессор
/// должен пробросить исключение вместо терминального Failed, чтобы Hangfire реально повторил
/// задачу (см. план, часть 1).</summary>
public record LegitimacyCheckResult(bool IsLegitimate, string? Reason, bool IsTransientFailure = false)
{
    public static LegitimacyCheckResult Legitimate() => new(true, null);

    public static LegitimacyCheckResult Rejected(string reason, bool isTransientFailure = false) => new(false, reason, isTransientFailure);
}

/// <summary>Первый обязательный шаг каждого enrich/extraction-конвейера — см. class doc
/// LegitimacyGuardService для полного описания deny-by-default гарантии.</summary>
public interface ILegitimacyGuardService
{
    Task<LegitimacyCheckResult> CheckAsync(string text, CancellationToken ct = default);

    Task<LegitimacyCheckResult> CheckAsync(
        string text, IReadOnlyList<(byte[] Bytes, string ContentType)> images, CancellationToken ct = default);
}
