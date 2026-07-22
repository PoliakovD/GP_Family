using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Infrastructure.Audit;

/// <summary>
/// Ретеншн аудита (задача 2.7 + backup-and-retention-policy): строки старше 12 месяцев
/// удаляются ежемесячной Hangfire-джобой — журнал не растёт бесконечно.
/// </summary>
public class AuditRetentionJob(AppDbContext db, ILogger<AuditRetentionJob> logger)
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(365);

    public async Task RunAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - Retention;
        var removed = await db.Set<MedicalAccessAudit>()
            .Where(a => a.OccurredAt < cutoff)
            .ExecuteDeleteAsync(ct);

        logger.LogInformation("AuditRetentionJob: удалено {Count} строк аудита старше {Cutoff}", removed, cutoff);
    }
}
