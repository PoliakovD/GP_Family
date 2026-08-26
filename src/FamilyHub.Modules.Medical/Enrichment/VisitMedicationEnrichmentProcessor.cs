using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Kb;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using FamilyHub.Infrastructure.Persistence;

namespace FamilyHub.Modules.Medical.Enrichment;

/// <summary>
/// Шаги 3-5 конвейера обогащения kb.global_medications_kb для препарата, упомянутого в заключении
/// врача (UX-редизайн) — зеркало MedicationEnrichmentProcessor БЕЗ семейного уведомления (у визита
/// нет FamilyId, см. VisitMedicationEnrichmentJob): тот же провайдер поиска, тот же кэш сниппетов,
/// тот же суммаризатор и тот же писатель справочника — второй набор задач не должен раздваивать
/// сам справочник или его качество, только контур учёта задачи.
/// </summary>
[Queue("enrichment")]
[AutomaticRetry(Attempts = 3, DelaysInSeconds = [60, 600, 3600])]
public class VisitMedicationEnrichmentProcessor(
    AppDbContext db,
    KbLookupService kbLookup,
    MedicationSearchCacheService searchCache,
    IMedicationSearchProvider provider,
    MedicationSummarizer summarizer,
    KbWriter kbWriter,
    EnrichmentQuotaService quota,
    ILogger<VisitMedicationEnrichmentProcessor> logger)
{
    /// <summary>Тот же порог, что MedicationEnrichmentProcessor — исправленное название должно
    /// быть очевидной опечаткой исходного, а не другим препаратом.</summary>
    private const double MinCorrectionSimilarity = 0.3;

    public async Task RunAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await db.VisitMedicationEnrichmentJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
        {
            logger.LogWarning("VisitMedicationEnrichmentJob {JobId} не найден — пропускаем.", jobId);
            return;
        }

        job.Attempts++;
        job.Status = EnrichmentJobStatus.Running;
        job.StartedAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        try
        {
            var existing = await kbLookup.LookupAsync(job.NormalizedName, ct);
            if (existing.Kind == KbLookupKind.Hit)
            {
                job.Status = EnrichmentJobStatus.Completed;
                job.KbId = existing.KbId;
                job.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                logger.LogInformation(
                    "VisitMedicationEnrichmentJob {JobId}: «{Name}» уже есть в справочнике, внешний запрос не понадобился.",
                    job.Id, job.NormalizedName);
                return;
            }

            var cached = provider.Name != "Null" ? await searchCache.GetCachedAsync(job.NormalizedName, ct) : null;

            IReadOnlyList<WebSnippet> snippets;
            if (cached is not null && cached.IsFresh)
            {
                snippets = cached.Snippets;
                job.Provider = cached.Provider;
            }
            else
            {
                if (provider.Name != "Null" && await quota.MonthlyQuotaExceededAsync(ct))
                {
                    job.Status = EnrichmentJobStatus.Skipped;
                    job.Error = "Месячная квота внешнего поиска исчерпана.";
                    job.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    logger.LogWarning("VisitMedicationEnrichmentJob {JobId}: месячная квота исчерпана, пропущено.", job.Id);
                    return;
                }

                snippets = await provider.SearchAsync(job.NormalizedName, WebSearchTopic.Medication, ct);
                if (provider.Name != "Null")
                {
                    job.ExternalSearchAt = DateTime.UtcNow;
                    job.Provider = provider.Name;
                    await db.SaveChangesAsync(ct);
                    await searchCache.RecordSearchAsync(job.NormalizedName, provider.Name, snippets, ct);
                }
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
            var (finalNormalizedName, finalDisplayName, extraAliases) = ResolveCorrectedName(job, summarized.Summary);

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var writeResult = await kbWriter.UpsertAsync(
                finalNormalizedName, finalDisplayName, summarized.Summary, source, extraAliases, ct);
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
            // Без события/уведомления (в отличие от MedicationEnrichmentProcessor) — у визита нет
            // семьи, которую можно было бы уведомить, а личный push «карточка препарата готова»
            // ради строки, которую пользователь сам не добавлял, был бы шумом; следующий просмотр
            // заключения врача просто увидит уже заполненную ссылку (см. ExtractionQueryService).
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            logger.LogInformation(
                "VisitMedicationEnrichmentJob {JobId}: справочник пополнен, «{Name}».", job.Id, finalDisplayName);
        }
        catch (Exception ex)
        {
            job.Error = ex.Message;
            await db.SaveChangesAsync(ct);
            logger.LogError(ex, "VisitMedicationEnrichmentJob {JobId} упал на попытке {Attempts} — Hangfire повторит.", job.Id, job.Attempts);
            throw;
        }
    }

    private (string NormalizedName, string DisplayName, IReadOnlyList<string>? ExtraAliases) ResolveCorrectedName(
        VisitMedicationEnrichmentJob job, MedicationSummary summary)
    {
        var correctedName = summary.CorrectedName?.Trim();
        if (string.IsNullOrEmpty(correctedName)) return (job.NormalizedName, job.SourceDisplayName, null);

        var correctedNormalized = MedicationNameNormalizer.Normalize(correctedName);
        if (correctedNormalized.Length == 0 || correctedNormalized == job.NormalizedName)
            return (job.NormalizedName, job.SourceDisplayName, null);

        var similarity = TrigramSimilarity.Similarity(correctedNormalized, job.NormalizedName);
        if (similarity < MinCorrectionSimilarity)
        {
            logger.LogWarning(
                "VisitMedicationEnrichmentJob {JobId}: модель предложила «{Corrected}» вместо «{Original}», " +
                "но схожесть {Similarity:F2} слишком низкая — коррекция отклонена.",
                job.Id, correctedName, job.SourceDisplayName, similarity);
            return (job.NormalizedName, job.SourceDisplayName, null);
        }

        return (correctedNormalized, correctedName, [job.NormalizedName]);
    }

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
