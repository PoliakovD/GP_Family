namespace FamilyHub.Modules.Medical.Pipeline;

/// <summary><see cref="IsTransientFailure"/> — отказ вызван технической недоступностью LM Studio
/// (см. LmStudioJsonResult.IsTransient), а не смысловым решением модели — вызывающий процессор
/// должен пробросить исключение вместо терминального Failed, чтобы Hangfire реально повторил
/// задачу (см. план, часть 1). <see cref="Confidence"/>/<see cref="ConfidenceReason"/> — оценка
/// модели 0..1 «насколько это реальное медицинское название» (ADR-0018, этап запроса): null,
/// если модель не вернула/вернула невалидное значение — потребитель трактует null как «ниже порога».</summary>
public record LegitimacyCheckResult(
    bool IsLegitimate, string? Reason, bool IsTransientFailure = false,
    double? Confidence = null, string? ConfidenceReason = null)
{
    public static LegitimacyCheckResult Legitimate(double? confidence = null, string? confidenceReason = null) =>
        new(true, null, false, confidence, confidenceReason);

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
