using FamilyHub.Domain.Entities;

namespace FamilyHub.Infrastructure.Enrichment;

/// <summary>Пороги уверенности модели для ручного одобрения (ADR-0018). Значения читаются БЕЗ
/// кеша (как <see cref="IWebSearchValveService"/>): правка порога в админке должна действовать
/// на следующей же задаче.</summary>
public interface IEnrichmentReviewConfigService
{
    Task<EnrichmentReviewThresholds> GetAsync(CancellationToken ct = default);

    /// <summary>Сохраняет пороги; значения должны быть в [0..1], иначе ArgumentOutOfRangeException.</summary>
    Task<EnrichmentReviewThresholds> SetAsync(EnrichmentReviewThresholds value, Guid? updatedByUserId, CancellationToken ct = default);
}

/// <summary>Вид справочника для выбора порога: препараты (аптечка и визиты) либо показатели анализов.</summary>
public enum EnrichmentReviewDomain
{
    Medication = 0,
    Analyte = 1,
}

public record EnrichmentReviewThresholds(
    double MedicationQueryMinConfidence,
    double AnalyteQueryMinConfidence,
    double MedicationResultMinConfidence,
    double AnalyteResultMinConfidence)
{
    public static EnrichmentReviewThresholds Defaults { get; } = new(
        EnrichmentReviewConfig.DefaultQueryMinConfidence,
        EnrichmentReviewConfig.DefaultQueryMinConfidence,
        EnrichmentReviewConfig.DefaultResultMinConfidence,
        EnrichmentReviewConfig.DefaultResultMinConfidence);

    public double QueryMin(EnrichmentReviewDomain domain) =>
        domain == EnrichmentReviewDomain.Medication ? MedicationQueryMinConfidence : AnalyteQueryMinConfidence;

    public double ResultMin(EnrichmentReviewDomain domain) =>
        domain == EnrichmentReviewDomain.Medication ? MedicationResultMinConfidence : AnalyteResultMinConfidence;

    /// <summary>true — уверенность достаточна. null (модель не вернула/невалидно) всегда НЕДОСТАТОЧНА
    /// (безопасный дефолт, ADR-0018): отсутствие оценки не должно пропускать задачу без человека.</summary>
    public static bool IsConfident(double? confidence, double threshold) =>
        confidence is { } c && c >= threshold;
}
