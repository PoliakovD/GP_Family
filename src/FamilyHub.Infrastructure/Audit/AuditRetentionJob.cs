using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Persistence;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Infrastructure.Audit;

/// <summary>
/// Ретеншн аудита (задача 2.7 + backup-and-retention-policy): строки старше 12 месяцев
/// удаляются ежемесячной Hangfire-джобой — журнал не растёт бесконечно.
///
/// AutomaticRetry — явно, тем же паттерном, что EncryptionRotationJob: DELETE по cutoff
/// идемпотентен по построению (повторный прогон просто ничего не находит), безопасный повтор
/// при транзиентном сбое — раньше жила на незадокументированном дефолте Hangfire (10 попыток).
/// </summary>
[AutomaticRetry(Attempts = 3, DelaysInSeconds = [60, 600, 3600])]
public class AuditRetentionJob(AppDbContext db, ILogger<AuditRetentionJob> logger)
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(365);

    /// <summary>Аудит платных вызовов веб-поиска (WebSearchCallLog, часть 2 плана
    /// ethereal-hugging-chipmunk) — окно короче основного аудита доступа: это диагностика "куда
    /// уходят деньги", не история доступа к персональным данным (152-ФЗ здесь не применяется, см.
    /// class doc WebSearchCallLog — не персональные данные), 180 дней достаточно для разбора
    /// расхождений в счетах провайдера.</summary>
    public static readonly TimeSpan WebSearchCallLogRetention = TimeSpan.FromDays(180);

    public async Task RunAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - Retention;
        var removed = await db.Set<MedicalAccessAudit>()
            .Where(a => a.OccurredAt < cutoff)
            .ExecuteDeleteAsync(ct);

        logger.LogInformation("AuditRetentionJob: удалено {Count} строк аудита старше {Cutoff}", removed, cutoff);

        var searchCallCutoff = DateTime.UtcNow - WebSearchCallLogRetention;
        var removedSearchCalls = await db.WebSearchCallLogs
            .Where(l => l.OccurredAt < searchCallCutoff)
            .ExecuteDeleteAsync(ct);

        logger.LogInformation(
            "AuditRetentionJob: удалено {Count} строк аудита платного поиска старше {Cutoff}",
            removedSearchCalls, searchCallCutoff);
    }
}
