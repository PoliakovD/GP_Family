using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Enrichment;
using FamilyHub.Modules.Medical.Kb;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Шаги обогащения справочника показателей (ветка medicalrecords) — повторная проверка справочника
/// → веб-поиск по лабораторным источникам (EnrichmentOptions.AnalyteTrustedDomains) → суммаризация
/// локальным Qwen → запись. Зеркало MedicationEnrichmentProcessor (этап 4), без шага коррекции
/// названия (CorrectedName) — имя показателя приходит из уже гейтованного LLM-извлечения
/// (LmStudioMedicalDocumentExtractor), а не из OCR по фото упаковки, опечатки маловероятны — и без
/// собственного кэша сниппетов (MedicationSearchCacheService): в отличие от медикаментов повторные
/// платные запросы по одному и тому же показателю редки (Pending/Running-дедуп уже достаточен).
/// Та же выделенная очередь "enrichment", тот же общий EnrichmentQuotaService (месячная квота —
/// одна на оба конвейера, они делят одного и того же внешнего провайдера).
/// </summary>
[Queue("enrichment")]
[AutomaticRetry(Attempts = 3, DelaysInSeconds = [60, 600, 3600])]
public class LabAnalyteEnrichmentProcessor(
    AppDbContext db,
    LabAnalyteKbLookupService kbLookup,
    IMedicationSearchProvider provider,
    LabAnalyteKbSummarizer summarizer,
    LabAnalyteKbWriter kbWriter,
    EnrichmentQuotaService quota,
    IBackgroundJobClient backgroundJobs,
    ILogger<LabAnalyteEnrichmentProcessor> logger)
{
    public async Task RunAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await db.LabAnalyteEnrichmentJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
        {
            logger.LogWarning("LabAnalyteEnrichmentJob {JobId} не найден — пропускаем.", jobId);
            return;
        }

        job.Attempts++;
        job.Status = EnrichmentJobStatus.Running;
        job.StartedAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        try
        {
            // Соседняя задача (другой анализ, тот же показатель) могла успеть наполнить справочник,
            // пока эта ждала своей очереди.
            var existing = await kbLookup.LookupAsync(job.NormalizedName, ct);
            if (existing.Kind == KbLookupKind.Hit)
            {
                job.Status = EnrichmentJobStatus.Completed;
                job.KbId = existing.KbId;
                job.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                logger.LogInformation(
                    "LabAnalyteEnrichmentJob {JobId}: «{Name}» уже есть в справочнике, внешний запрос не понадобился.",
                    job.Id, job.NormalizedName);
                backgroundJobs.Enqueue<RecalculateIndicatorFlagsJob>(j => j.RunAsync(existing.KbId!.Value, CancellationToken.None));
                return;
            }

            if (provider.Name != "Null" && await quota.MonthlyQuotaExceededAsync(ct))
            {
                job.Status = EnrichmentJobStatus.Skipped;
                job.Error = "Месячная квота внешнего поиска исчерпана.";
                job.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                logger.LogWarning("LabAnalyteEnrichmentJob {JobId}: месячная квота исчерпана, пропущено.", job.Id);
                return;
            }

            var snippets = await provider.SearchAsync(job.NormalizedName, WebSearchTopic.LabAnalyte, ct);
            if (provider.Name != "Null")
            {
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
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // Дозаполнение задним числом (каскад п.1a) — показатели, распознанные до того, как
            // справочник узнал этот аналит, сейчас застряли на RefSource.None.
            backgroundJobs.Enqueue<RecalculateIndicatorFlagsJob>(j => j.RunAsync(writeResult.KbId!.Value, CancellationToken.None));

            logger.LogInformation(
                "LabAnalyteEnrichmentJob {JobId}: справочник показателей пополнен, «{Name}».", job.Id, job.SourceDisplayName);
        }
        catch (Exception ex)
        {
            job.Error = ex.Message;
            await db.SaveChangesAsync(ct);
            logger.LogError(ex, "LabAnalyteEnrichmentJob {JobId} упал на попытке {Attempts} — Hangfire повторит.", job.Id, job.Attempts);
            throw;
        }
    }

    /// <summary>"brave: helix.ru, invitro.ru" — провайдер + реально использованные модельным ответом домены.</summary>
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
