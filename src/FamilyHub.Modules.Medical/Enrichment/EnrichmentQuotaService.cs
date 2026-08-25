using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FamilyHub.Modules.Medical.Enrichment;

/// <summary>
/// Месячная квота внешнего платного поиска (EnrichmentOptions.MonthlyQuota) — общая на ОБА
/// конвейера обогащения (медикаменты + ветка medicalrecords: лабораторные показатели), потому что
/// оба тратят одну и ту же квоту одного и того же провайдера (Brave/Yandex). Вынесено из
/// MedicationEnrichmentProcessor.MonthlyQuotaExceededAsync при добавлении второго потребителя —
/// раньше каждый конвейер считал свою таблицу задач независимо, что позволяло вдвое превысить
/// реальный лимит провайдера.
/// </summary>
public class EnrichmentQuotaService(AppDbContext db, IOptions<EnrichmentOptions> options)
{
    public async Task<bool> MonthlyQuotaExceededAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var medicationCount = await db.MedicationEnrichmentJobs
            .CountAsync(j => j.ExternalSearchAt != null && j.ExternalSearchAt >= monthStart, ct);
        var analyteCount = await db.LabAnalyteEnrichmentJobs
            .CountAsync(j => j.ExternalSearchAt != null && j.ExternalSearchAt >= monthStart, ct);

        return medicationCount + analyteCount >= options.Value.MonthlyQuota;
    }
}
