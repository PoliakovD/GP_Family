using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Infrastructure.Enrichment;

/// <summary>Пороги ручного одобрения (ADR-0018) — единственная строка EnrichmentReviewConfig,
/// отсутствие строки = значения по умолчанию. Без кеша — один индексированный SELECT на задачу,
/// дешевле любого платного вызова/ревью, которые пороги гейтят.</summary>
public class EnrichmentReviewConfigService(AppDbContext db) : IEnrichmentReviewConfigService
{
    public async Task<EnrichmentReviewThresholds> GetAsync(CancellationToken ct = default)
    {
        var row = await db.EnrichmentReviewConfigs.AsNoTracking().FirstOrDefaultAsync(ct);
        return row is null
            ? EnrichmentReviewThresholds.Defaults
            : new EnrichmentReviewThresholds(
                row.MedicationQueryMinConfidence,
                row.AnalyteQueryMinConfidence,
                row.MedicationResultMinConfidence,
                row.AnalyteResultMinConfidence);
    }

    public async Task<EnrichmentReviewThresholds> SetAsync(
        EnrichmentReviewThresholds value, Guid? updatedByUserId, CancellationToken ct = default)
    {
        foreach (var v in new[]
                 {
                     value.MedicationQueryMinConfidence, value.AnalyteQueryMinConfidence,
                     value.MedicationResultMinConfidence, value.AnalyteResultMinConfidence,
                 })
        {
            if (double.IsNaN(v) || v < 0 || v > 1)
                throw new ArgumentOutOfRangeException(nameof(value), "Порог уверенности должен быть в диапазоне 0..1.");
        }

        var row = await db.EnrichmentReviewConfigs.FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = new EnrichmentReviewConfig { Id = Guid.NewGuid() };
            db.EnrichmentReviewConfigs.Add(row);
        }

        row.MedicationQueryMinConfidence = value.MedicationQueryMinConfidence;
        row.AnalyteQueryMinConfidence = value.AnalyteQueryMinConfidence;
        row.MedicationResultMinConfidence = value.MedicationResultMinConfidence;
        row.AnalyteResultMinConfidence = value.AnalyteResultMinConfidence;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedByUserId = updatedByUserId;
        await db.SaveChangesAsync(ct);
        return value;
    }
}
