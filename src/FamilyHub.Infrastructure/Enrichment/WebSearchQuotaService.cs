using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Enrichment;

/// <summary>
/// Месячная квота на платные вызовы внешнего веб-поиска (ADR-0005 §9) — считается прямо по
/// WebSearchCallLog (Outcome != CacheHit — см. WebSearchCallOutcome), не по отдельному счётчику:
/// таблица аудита уже есть и уже пишется на каждый реальный вызов (WebSearchCallLogger), заводить
/// параллельный счётчик было бы дублированием источника истины. Единая квота на все три конвейера
/// (LabAnalyteEnrichmentProcessor/MedicationEnrichmentProcessor/VisitMedicationEnrichmentProcessor) —
/// они делят одного провайдера (EnrichmentOptions.Provider), отдельный счётчик на конвейер позволил
/// бы вдвое-втрое превысить реальный лимит (тот же аргумент, что и в исходном ADR-0005 §9).
/// Считается в Postgres, переживает рестарт процесса (ADR-0001).
/// </summary>
public class WebSearchQuotaService(AppDbContext db, IOptions<EnrichmentOptions> options)
{
    /// <summary>0 в EnrichmentOptions.MonthlyQuota означает "без лимита" — не спрашиваем БД впустую.</summary>
    public async Task<bool> MonthlyQuotaExceededAsync(CancellationToken ct = default)
    {
        var quota = options.Value.MonthlyQuota;
        if (quota <= 0) return false;

        var monthStartUtc = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var usedThisMonth = await db.WebSearchCallLogs.AsNoTracking()
            .CountAsync(l => l.OccurredAt >= monthStartUtc && l.Outcome != Domain.Enums.WebSearchCallOutcome.CacheHit, ct);
        return usedThisMonth >= quota;
    }
}
