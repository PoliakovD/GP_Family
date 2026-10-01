using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Kb;
using FamilyHub.Modules.Medical.Pipeline;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
[AutomaticRetry(Attempts = VisitMedicationEnrichmentProcessor.MaxAttempts, DelaysInSeconds = [60, 600, 3600])]
public class VisitMedicationEnrichmentProcessor(
    AppDbContext db,
    KbLookupService kbLookup,
    MedicationSearchCacheService searchCache,
    IMedicationSearchProvider provider,
    WebSearchCallLogger callLogger,
    IWebSearchValveService searchValve,
    IEnrichmentReviewConfigService reviewConfig,
    ILegitimacyGuardService legitimacyGuard,
    MedicationSummarizer summarizer,
    KbWriter kbWriter,
    EnrichmentTrustedDomainService trustedDomains,
    IOptions<EnrichmentOptions> options,
    ILogger<VisitMedicationEnrichmentProcessor> logger)
{
    /// <summary>Должно совпадать с Attempts в [AutomaticRetry] — см. MedicalDocumentExtractionProcessor.MaxAttempts.</summary>
    public const int MaxAttempts = 3;

    public async Task RunAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await db.VisitMedicationEnrichmentJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
        {
            logger.LogWarning("VisitMedicationEnrichmentJob {JobId} не найден — пропускаем.", jobId);
            return;
        }

        // Ambient-контекст для живого потока "мыслей" (план) — см. class doc LmStudioThinkingContext.
        using var _ = LmStudioThinkingContext.Begin(LlmJobKind.VisitMedicationEnrichment, job.Id);

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

            IReadOnlyList<WebSnippet> rawSnippets;
            IReadOnlyDictionary<string, bool>? overrides = null;
            if (cached is not null && cached.IsFresh)
            {
                rawSnippets = cached.Snippets;
                overrides = cached.Overrides;
                job.Provider = cached.Provider;
                // Кэш-хит — не платный вызов, но строка в аудит-логе всё равно нужна (см. class doc
                // WebSearchCallOutcome.CacheHit) — иначе нельзя проверить, что кэш реально работает.
                await callLogger.LogAsync(new WebSearchCallLogEntry(
                    cached.Provider, WebSearchTopic.Medication, job.NormalizedName, null, string.Empty, null, null, 0,
                    WebSearchCallOutcome.CacheHit, cached.Snippets.Count, null, null,
                    nameof(LlmJobKind.VisitMedicationEnrichment), job.Id), ct);
            }
            else
            {
                if (provider.Name != "Null")
                {
                    // Гейт 1 (ADR-0018): каждый платный поиск ждёт ручного одобрения админа. В отличие
                    // от MedicationEnrichmentProcessor у этого конвейера нет собственного шага
                    // легитимности, поэтому оценку этапа запроса снимаем здесь — только когда она
                    // реально нужна (задача ещё не одобрена). Отказ стража НЕ останавливает задачу
                    // (поведение конвейера не меняем): он превращается в уверенность 0 с причиной —
                    // админ увидит красную строку и решит сам. Технический сбой — ретрай Hangfire.
                    if (job.SearchApprovedAt is null)
                    {
                        var guard = await legitimacyGuard.CheckAsync(job.SourceDisplayName, ct);
                        if (!guard.IsLegitimate && guard.IsTransientFailure)
                            throw new LmStudioUnavailableException(guard.Reason ?? "Локальный сервер распознавания недоступен.");

                        var confidence = guard.IsLegitimate ? guard.Confidence : 0d;
                        var reason = guard.IsLegitimate ? guard.ConfidenceReason : guard.Reason;
                        EnrichmentReviewGate.TryParkForSearchApproval(job, confidence, reason);
                        await db.SaveChangesAsync(ct); // НЕ CompletedAt: ждёт админа; return без исключения — как Deferred
                        logger.LogInformation(
                            "VisitMedicationEnrichmentJob {JobId}: платный поиск ждёт одобрения админа (уверенность {Confidence}).",
                            job.Id, job.QueryConfidence);
                        return;
                    }

                    // Вентиль платного поиска (ADR-0005 §9) — поверх одобрения (см. MedicationEnrichmentProcessor).
                    if (await searchValve.IsPausedAsync(ct))
                    {
                        job.Status = EnrichmentJobStatus.Deferred;
                        job.Error = "Платный веб-поиск на паузе — задача отложена до его включения.";
                        await db.SaveChangesAsync(ct); // НЕ CompletedAt: задача не завершена, а отложена
                        logger.LogInformation("VisitMedicationEnrichmentJob {JobId}: отложена — вентиль платного поиска закрыт.", job.Id);
                        return;
                    }
                }

                var callContext = new WebSearchCallContext(nameof(LlmJobKind.VisitMedicationEnrichment), job.Id);
                rawSnippets = await provider.SearchAsync(
                    EnrichmentReviewGate.EffectiveQuery(job), WebSearchTopic.Medication, ct: ct, callContext: callContext);
                if (provider.Name != "Null")
                {
                    job.ExternalSearchAt = DateTime.UtcNow;
                    job.Provider = provider.Name;
                    await db.SaveChangesAsync(ct);
                    await searchCache.RecordSearchAsync(job.NormalizedName, provider.Name, rawSnippets, ct);
                }
            }

            // См. MedicationEnrichmentProcessor doc — фильтрация по доверенным доменам переехала
            // на процессор (БД-список EnrichmentTrustedDomain + override'ы конкретных URL).
            var domains = await trustedDomains.GetActiveDomainsByPriorityAsync(WebSearchTopic.Medication, ct);
            var snippets = EnrichmentSnippetFilter.SelectForSummary(rawSnippets, domains, overrides, options.Value.MaxSnippets);

            // См. MedicationEnrichmentProcessor — проверяем ДО суммаризатора, единственный воркер
            // очереди enrichment не должен тратиться на вызов LLM, заведомо обречённый на отказ.
            if (snippets.Count == 0)
            {
                job.Status = EnrichmentJobStatus.Failed;
                job.Error = "Нет сниппетов от доверенных источников — суммаризировать нечего.";
                job.FailureReason = EnrichmentFailureReason.NoTrustedSnippets;
                job.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            var summarized = await summarizer.SummarizeAsync(job.SourceDisplayName, snippets, ct);
            if (!summarized.Success || summarized.Summary is null)
            {
                job.Status = EnrichmentJobStatus.Failed;
                job.Error = summarized.Error;
                job.FailureReason = summarized.Reason;
                job.IsTransientFailure = summarized.Reason == EnrichmentFailureReason.LmStudioUnavailable;
                job.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            var source = EnrichmentReviewGate.BuildSourceLabel(provider.Name, snippets, summarized.Summary.UsedSourceIndexes);
            var (finalNormalizedName, finalDisplayName, extraAliases) = ResolveCorrectedName(job, summarized.Summary);

            // Гейт 2 (ADR-0018) — см. MedicationEnrichmentProcessor.
            var thresholds = await reviewConfig.GetAsync(ct);
            if (EnrichmentReviewGate.NeedsResultReview(
                    summarized.Confidence, thresholds.ResultMin(EnrichmentReviewDomain.Medication)))
            {
                var draft = new MedicationDraft(
                    finalNormalizedName, finalDisplayName, extraAliases, source, summarized.Summary,
                    EnrichmentDraftSerializer.ToDraftSnippets(snippets), summarized.FieldSources);
                EnrichmentReviewGate.ParkForResultReview(
                    job, EnrichmentDraftSerializer.Serialize(draft), summarized.Confidence, summarized.ConfidenceReason);
                await db.SaveChangesAsync(ct);
                logger.LogInformation(
                    "VisitMedicationEnrichmentJob {JobId}: уверенность результата {Confidence} ниже порога — черновик ждёт ревью админа.",
                    job.Id, summarized.Confidence);
                return;
            }

            job.ResultConfidence = summarized.Confidence;
            job.ResultConfidenceReason = summarized.ConfidenceReason;

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var writeResult = await kbWriter.UpsertAsync(
                finalNormalizedName, finalDisplayName, summarized.Summary, source, extraAliases, ct);
            if (!writeResult.Success)
            {
                await tx.RollbackAsync(ct);
                job.Status = EnrichmentJobStatus.Failed;
                job.Error = writeResult.RejectionReason;
                job.FailureReason = EnrichmentFailureReason.IsolationViolation;
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
            if (job.Attempts >= MaxAttempts)
            {
                job.Status = EnrichmentJobStatus.Failed;
                job.CompletedAt = DateTime.UtcNow;
                job.IsTransientFailure = ex is LmStudioUnavailableException;
                job.FailureReason = ex is LmStudioUnavailableException
                    ? EnrichmentFailureReason.LmStudioUnavailable
                    : EnrichmentFailureReason.Unknown;
            }
            await db.SaveChangesAsync(ct);
            logger.LogError(ex, "VisitMedicationEnrichmentJob {JobId} упал на попытке {Attempts} — Hangfire повторит.", job.Id, job.Attempts);
            throw;
        }
    }

    private (string NormalizedName, string DisplayName, IReadOnlyList<string>? ExtraAliases) ResolveCorrectedName(
        VisitMedicationEnrichmentJob job, MedicationSummary summary)
    {
        var resolution = MedicationNameCorrection.Resolve(job.NormalizedName, job.SourceDisplayName, summary);
        if (resolution.Outcome == MedicationNameCorrectionOutcome.RejectedLowSimilarity)
        {
            logger.LogWarning(
                "VisitMedicationEnrichmentJob {JobId}: модель предложила «{Corrected}» вместо «{Original}», " +
                "но схожесть {Similarity:F2} слишком низкая — коррекция отклонена.",
                job.Id, resolution.CorrectedName, job.SourceDisplayName, resolution.Similarity);
        }

        return (resolution.NormalizedName, resolution.DisplayName, resolution.ExtraAliases);
    }
}
