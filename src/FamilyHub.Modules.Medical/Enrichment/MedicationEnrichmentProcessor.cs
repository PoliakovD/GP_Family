using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.Outbox;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Kb;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Modules.Medical.Enrichment;

/// <summary>
/// Шаги 3-5 конвейера обогащения (этап 4): повторная проверка справочника → веб-поиск → суммаризация
/// локальным Qwen → запись + событие. Выполняется в выделенной Hangfire-очереди "enrichment" с
/// одним воркером (см. Program.cs) — естественно укладывается в лимит Brave free-tier (1 req/s),
/// не забирая пропускную способность у ReminderScanJob/AuditRetentionJob. AutomaticRetry — только
/// на настоящие сбои (сеть к БД, необработанное исключение); ожидаемые исходы (нет доверенных
/// источников, квота исчерпана) переводят статус задачи в Failed/Skipped и возвращаются обычным
/// return — Hangfire не должен их ретраить.
/// </summary>
[Queue("enrichment")]
[AutomaticRetry(Attempts = 3, DelaysInSeconds = [60, 600, 3600])]
public class MedicationEnrichmentProcessor(
    AppDbContext db,
    KbLookupService kbLookup,
    IMedicationSearchProvider provider,
    MedicationSummarizer summarizer,
    KbWriter kbWriter,
    IOutboxWriter outbox,
    IOptions<EnrichmentOptions> options,
    ILogger<MedicationEnrichmentProcessor> logger)
{
    public async Task RunAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await db.MedicationEnrichmentJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
        {
            logger.LogWarning("MedicationEnrichmentJob {JobId} не найден — пропускаем.", jobId);
            return;
        }

        job.Attempts++;
        job.Status = EnrichmentJobStatus.Running;
        job.StartedAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        try
        {
            // Соседняя задача (другая семья, тот же препарат) могла успеть наполнить справочник,
            // пока эта ждала своей очереди — тогда внешний запрос вообще не нужен.
            var existing = await kbLookup.LookupAsync(job.NormalizedName, ct);
            if (existing.Kind == KbLookupKind.Hit)
            {
                job.Status = EnrichmentJobStatus.Completed;
                job.KbId = existing.KbId;
                job.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                logger.LogInformation(
                    "MedicationEnrichmentJob {JobId}: «{Name}» уже есть в справочнике, внешний запрос не понадобился.",
                    job.Id, job.NormalizedName);
                return;
            }

            if (provider.Name != "Null" && await MonthlyQuotaExceededAsync(ct))
            {
                job.Status = EnrichmentJobStatus.Skipped;
                job.Error = "Месячная квота внешнего поиска исчерпана.";
                job.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                logger.LogWarning("MedicationEnrichmentJob {JobId}: месячная квота исчерпана, пропущено.", job.Id);
                return;
            }

            var snippets = await provider.SearchAsync(job.NormalizedName, ct);
            if (provider.Name != "Null")
            {
                // Считаем реальным внешним запросом только настоящих провайдеров — Null ничего
                // никуда не отправляет, засчитывать его в месячную квоту незачем.
                job.ExternalSearchAt = DateTime.UtcNow;
                job.Provider = provider.Name;
                await db.SaveChangesAsync(ct);
            }

            var summarized = await summarizer.SummarizeAsync(job.SourceDisplayName, snippets, ct);
            if (!summarized.Success || summarized.Summary is null)
            {
                job.Status = EnrichmentJobStatus.Failed;
                job.Error = summarized.Error;
                job.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            var source = BuildSourceLabel(provider.Name, snippets, summarized.Summary.UsedSourceIndexes);

            // Явная транзакция: raw SQL upsert в kb (KbWriter) и обновление статуса задачи +
            // публикация события должны либо оба закоммититься, либо оба откатиться.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var writeResult = await kbWriter.UpsertAsync(job.NormalizedName, job.SourceDisplayName, summarized.Summary, source, ct);
            if (!writeResult.Success)
            {
                await tx.RollbackAsync(ct);
                job.Status = EnrichmentJobStatus.Failed;
                job.Error = writeResult.RejectionReason;
                job.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            job.Status = EnrichmentJobStatus.Completed;
            job.KbId = writeResult.KbId;
            job.CompletedAt = DateTime.UtcNow;
            outbox.Enqueue(new MedicationEnrichedEvent(
                job.Id, writeResult.KbId!.Value, job.SourceDisplayName, job.RequestedByUserId, job.FamilyId));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            logger.LogInformation(
                "MedicationEnrichmentJob {JobId}: справочник пополнен, «{Name}».", job.Id, job.SourceDisplayName);
        }
        catch (Exception ex)
        {
            job.Error = ex.Message;
            await db.SaveChangesAsync(ct);
            logger.LogError(ex, "MedicationEnrichmentJob {JobId} упал на попытке {Attempts} — Hangfire повторит.", job.Id, job.Attempts);
            throw;
        }
    }

    private async Task<bool> MonthlyQuotaExceededAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var usedThisMonth = await db.MedicationEnrichmentJobs
            .CountAsync(j => j.ExternalSearchAt != null && j.ExternalSearchAt >= monthStart, ct);
        return usedThisMonth >= options.Value.MonthlyQuota;
    }

    /// <summary>"brave: vidal.ru, rlsnet.ru" — провайдер + реально использованные модельным ответом домены.</summary>
    private static string BuildSourceLabel(string providerName, IReadOnlyList<WebSnippet> snippets, IReadOnlyList<int> usedIndexes)
    {
        var domains = usedIndexes
            .Where(i => i >= 0 && i < snippets.Count)
            .Select(i => Uri.TryCreate(snippets[i].Url, UriKind.Absolute, out var uri) ? uri.Host : null)
            .Where(host => host is not null)
            .Distinct()
            .ToList();

        return domains.Count == 0 ? providerName : $"{providerName}: {string.Join(", ", domains)}";
    }
}
