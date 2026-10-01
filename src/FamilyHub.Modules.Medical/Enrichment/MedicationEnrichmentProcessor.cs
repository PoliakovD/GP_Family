using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Messaging;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Kb;
using FamilyHub.Modules.Medical.Pipeline;
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
/// источников) переводят статус задачи в Failed и возвращаются обычным return — Hangfire не должен
/// их ретраить.
/// </summary>
[Queue("enrichment")]
[AutomaticRetry(Attempts = MedicationEnrichmentProcessor.MaxAttempts, DelaysInSeconds = [60, 600, 3600])]
public class MedicationEnrichmentProcessor(
    AppDbContext db,
    KbLookupService kbLookup,
    MedicationSearchCacheService searchCache,
    IMedicationSearchProvider provider,
    WebSearchCallLogger callLogger,
    IWebSearchValveService searchValve,
    IEnrichmentReviewConfigService reviewConfig,
    MedicationSummarizer summarizer,
    KbWriter kbWriter,
    EnrichmentTrustedDomainService trustedDomains,
    ILegitimacyGuardService legitimacyGuard,
    IOptions<EnrichmentOptions> options,
    IDomainEventPublisher publisher,
    ILogger<MedicationEnrichmentProcessor> logger)
{
    /// <summary>Должно совпадать с Attempts в [AutomaticRetry] — на последней попытке catch-блок
    /// ниже сам переводит job в Failed, иначе строка навсегда остаётся в Running и частичный
    /// уникальный индекс (Status IN (0,1)) перманентно блокирует повторную постановку в очередь
    /// (см. аудит, находка Critical #3).</summary>
    public const int MaxAttempts = 3;

    public async Task RunAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await db.MedicationEnrichmentJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
        {
            logger.LogWarning("MedicationEnrichmentJob {JobId} не найден — пропускаем.", jobId);
            return;
        }

        // Ambient-контекст для живого потока "мыслей" (план) — см. class doc LmStudioThinkingContext.
        using var _ = LmStudioThinkingContext.Begin(LlmJobKind.MedicationEnrichment, job.Id);

        job.Attempts++;
        job.Status = EnrichmentJobStatus.Running;
        job.StartedAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        try
        {
            // Первый обязательный шаг (PipelineCatalog.LegitimacyCheckStep) — ДО любого обращения к
            // справочнику или внешнему поиску: SourceDisplayName — свободный текст (распознан по
            // фото упаковки или введён вручную), который дальше попадёт и в поисковый запрос, и в
            // промпт суммаризатора.
            var guardResult = await legitimacyGuard.CheckAsync(job.SourceDisplayName, ct);
            if (!guardResult.IsLegitimate)
            {
                // Технический отказ гейта (LM Studio недоступен) — пробрасываем исключение, чтобы
                // Hangfire реально повторил задачу, вместо терминального Failed с первой попытки
                // (см. план, часть 1).
                if (guardResult.IsTransientFailure)
                    throw new LmStudioUnavailableException(guardResult.Reason ?? "Локальный сервер распознавания недоступен.");

                job.Status = EnrichmentJobStatus.Failed;
                job.Error = guardResult.Reason;
                job.FailureReason = EnrichmentFailureReason.Legitimacy;
                job.CompletedAt = DateTime.UtcNow;
                await PublishFailureAsync(job, ct);
                await db.SaveChangesAsync(ct);
                logger.LogWarning(
                    "MedicationEnrichmentJob {JobId} остановлена проверкой легитимности: {Reason}", job.Id, guardResult.Reason);
                return;
            }

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

            // Настоящий кэш, не просто лог "когда можно/нельзя": если по этому названию уже есть
            // сохранённые сниппеты и минимальный интервал обновления (EnrichmentOptions.
            // MinRefreshIntervalMonths) ещё не истёк — переиспользуем их и НЕ ходим к платному API
            // повторно. Это даёт пересчитывать summarize сколько угодно раз (например, при
            // доработке промпта/схемы полей MedicationSummary в разработке), не тратя квоту на
            // одно и то же название снова и снова.
            var cached = provider.Name != "Null" ? await searchCache.GetCachedAsync(job.NormalizedName, ct) : null;

            IReadOnlyList<WebSnippet> rawSnippets;
            IReadOnlyDictionary<string, bool>? overrides = null;
            if (cached is not null && cached.IsFresh)
            {
                rawSnippets = cached.Snippets;
                overrides = cached.Overrides;
                job.Provider = cached.Provider;
                logger.LogInformation(
                    "MedicationEnrichmentJob {JobId}: «{Name}» — использованы закэшированные результаты поиска " +
                    "от {LastUpdatedAt:dd.MM.yyyy}, платный запрос не потребовался.",
                    job.Id, job.NormalizedName, cached.LastUpdatedAt);
                // Кэш-хит — не платный вызов, но строка в аудит-логе всё равно нужна (см. class doc
                // WebSearchCallOutcome.CacheHit) — иначе нельзя проверить, что кэш реально работает.
                await callLogger.LogAsync(new WebSearchCallLogEntry(
                    cached.Provider, WebSearchTopic.Medication, job.NormalizedName, null, string.Empty, null, null, 0,
                    WebSearchCallOutcome.CacheHit, cached.Snippets.Count, null, null,
                    nameof(LlmJobKind.MedicationEnrichment), job.Id), ct);
            }
            else
            {
                if (provider.Name != "Null")
                {
                    // Гейт 1 (ADR-0018): КАЖДЫЙ платный поиск ждёт ручного одобрения админа. Кэш-хит
                    // (ветка выше) бесплатен и сюда не попадает. Уверенность этапа запроса — оценка
                    // стража легитимности выше; null = «ниже порога» решает очередь, не процессор.
                    if (EnrichmentReviewGate.TryParkForSearchApproval(job, guardResult.Confidence, guardResult.ConfidenceReason))
                    {
                        await db.SaveChangesAsync(ct); // НЕ CompletedAt: ждёт админа; return без исключения — как Deferred
                        logger.LogInformation(
                            "MedicationEnrichmentJob {JobId}: платный поиск ждёт одобрения админа (уверенность {Confidence}).",
                            job.Id, job.QueryConfidence);
                        return;
                    }

                    // Вентиль платного поиска (ADR-0005 §9) — поверх одобрения: одобренная задача
                    // при закрытом вентиле уходит в Deferred, DeferredEnrichmentReleaseJob вернёт её
                    // в Pending при открытии (SearchApprovedAt сохранён — повторно не спросим).
                    if (await searchValve.IsPausedAsync(ct))
                    {
                        job.Status = EnrichmentJobStatus.Deferred;
                        job.Error = "Платный веб-поиск на паузе — задача отложена до его включения.";
                        await db.SaveChangesAsync(ct); // НЕ CompletedAt: задача не завершена, а отложена
                        logger.LogInformation("MedicationEnrichmentJob {JobId}: отложена — вентиль платного поиска закрыт.", job.Id);
                        return;
                    }
                }

                var callContext = new WebSearchCallContext(nameof(LlmJobKind.MedicationEnrichment), job.Id);
                // Текст запроса — правка админа (ProposedQueryText) либо нормализованное имя.
                rawSnippets = await provider.SearchAsync(
                    EnrichmentReviewGate.EffectiveQuery(job), WebSearchTopic.Medication, ct: ct, callContext: callContext);
                if (provider.Name != "Null")
                {
                    // Считаем реальным внешним запросом только настоящих провайдеров — Null ничего
                    // никуда не отправляет, засчитывать его в кулдаун незачем.
                    job.ExternalSearchAt = DateTime.UtcNow;
                    job.Provider = provider.Name;
                    await db.SaveChangesAsync(ct);
                    // Платная квота уже потрачена независимо от исхода суммаризации ниже — кэшируем
                    // ВСЕ сниппеты (не только доверенные — пересборка enrich-пайплайна) и кулдаун
                    // сразу после запроса, а не после успешной записи в справочник.
                    await searchCache.RecordSearchAsync(job.NormalizedName, provider.Name, rawSnippets, ct);
                }
            }

            // Фильтрация по доверенным доменам (БД-список, управляемый через админку) + точечные
            // override'ы конкретных URL — переехала сюда с провайдера (пересборка enrich-пайплайна):
            // так смена списка/override не требует нового платного запроса, только пересчёта поверх
            // уже закэшированных сырых сниппетов.
            var domains = await trustedDomains.GetActiveDomainsByPriorityAsync(WebSearchTopic.Medication, ct);
            var snippets = EnrichmentSnippetFilter.SelectEnabled(rawSnippets, domains, overrides)
                .Take(options.Value.MaxSnippets)
                .ToList();

            // См. LabAnalyteEnrichmentProcessor — проверяем ДО суммаризатора, не полагаемся на его
            // собственную (тоже верную) проверку: единственный воркер очереди enrichment не должен
            // тратиться на вызов LLM, заведомо обречённый на тот же отказ.
            if (snippets.Count == 0)
            {
                job.Status = EnrichmentJobStatus.Failed;
                job.Error = "Нет сниппетов от доверенных источников — суммаризировать нечего.";
                job.FailureReason = EnrichmentFailureReason.NoTrustedSnippets;
                job.CompletedAt = DateTime.UtcNow;
                await PublishFailureAsync(job, ct);
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
                await PublishFailureAsync(job, ct);
                await db.SaveChangesAsync(ct);
                return;
            }

            var source = MedicationNameCorrection.BuildSourceLabel(provider.Name, snippets, summarized.Summary.UsedSourceIndexes);
            var (finalNormalizedName, finalDisplayName, extraAliases) = ResolveCorrectedName(job, summarized.Summary);

            // Гейт 2 (ADR-0018): уверенность суммаризатора ниже порога (или не вернулась) — в kb НЕ
            // пишем, сохраняем черновик и ждём админа (одобрить/поправить/пересуммаризировать/отклонить).
            var thresholds = await reviewConfig.GetAsync(ct);
            if (EnrichmentReviewGate.NeedsResultReview(
                    summarized.Confidence, thresholds.ResultMin(EnrichmentReviewDomain.Medication)))
            {
                var draft = new MedicationDraft(
                    finalNormalizedName, finalDisplayName, extraAliases, source, summarized.Summary,
                    EnrichmentDraftSerializer.ToDraftSnippets(snippets));
                EnrichmentReviewGate.ParkForResultReview(
                    job, EnrichmentDraftSerializer.Serialize(draft), summarized.Confidence, summarized.ConfidenceReason);
                await db.SaveChangesAsync(ct);
                logger.LogInformation(
                    "MedicationEnrichmentJob {JobId}: уверенность результата {Confidence} ниже порога — черновик ждёт ревью админа.",
                    job.Id, summarized.Confidence);
                return;
            }

            job.ResultConfidence = summarized.Confidence;
            job.ResultConfidenceReason = summarized.ConfidenceReason;

            // Явная транзакция: raw SQL upsert в kb (KbWriter) и обновление статуса задачи +
            // публикация события должны либо оба закоммититься, либо оба откатиться.
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
                await PublishFailureAsync(job, ct);
                await db.SaveChangesAsync(ct);
                return;
            }

            job.Status = EnrichmentJobStatus.Completed;
            job.KbId = writeResult.KbId;
            job.CompletedAt = DateTime.UtcNow;
            // Публикация внутри явной транзакции: outbox-строка коммитится вместе с ней
            // (корректно — тот же AppDbContext), но delivery-service шины "будится" сразу после
            // SaveChangesAsync, ДО commit — строку он ещё не увидит и подхватит только на
            // следующем тике Messaging:Outbox:QueryDelay. Не ошибка, просто небольшая задержка.
            var medkitId = await ResolveMedkitIdAsync(job.MedicationId, ct);
            await publisher.PublishAsync(new MedicationEnrichedEvent(
                job.Id, writeResult.KbId!.Value, finalDisplayName, job.RequestedByUserId, job.FamilyId, medkitId), ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            logger.LogInformation(
                "MedicationEnrichmentJob {JobId}: справочник пополнен, «{Name}».", job.Id, finalDisplayName);
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
                await PublishFailureAsync(job, ct);
            }
            await db.SaveChangesAsync(ct);
            logger.LogError(ex, "MedicationEnrichmentJob {JobId} упал на попытке {Attempts} — Hangfire повторит.", job.Id, job.Attempts);
            throw;
        }
    }

    /// <summary>Уведомляем только о настоящем терминальном отказе — техническую недоступность LM
    /// Studio LmStudioRecoverySweepJob резюмирует молча в течение 7 дней (см. IsTransientFailure),
    /// сообщать "не удалось" в этот момент было бы дезинформацией. Вызывается из пяти мест, где
    /// job окончательно уходит в Failed (четыре штатных исхода внутри try — гейт легитимности,
    /// нет доверенных сниппетов, суммаризатор отказал, изоляция справочника — плюс сам catch на
    /// последней попытке); все нужные поля (RequestedByUserId/FamilyId/SourceDisplayName) уже на
    /// самой job, отдельный запрос не нужен (в отличие от MedicalDocumentExtractionProcessor, где
    /// OwnerUserId лежит на записи, не на job) — кроме MedkitId для клик-через ниже, которого на
    /// job нет вовсе (только справочный MedicationId).</summary>
    private async Task PublishFailureAsync(MedicationEnrichmentJob job, CancellationToken ct)
    {
        if (job.IsTransientFailure) return;
        var medkitId = await ResolveMedkitIdAsync(job.MedicationId, ct);
        await publisher.PublishAsync(
            new MedicationEnrichmentFailedEvent(job.Id, job.SourceDisplayName, job.RequestedByUserId, job.FamilyId, medkitId), ct);
    }

    /// <summary>MedicationId на job — справочный, не FK (см. класс-doc MedicationEnrichmentJob):
    /// медикамент мог быть удалён между сохранением и завершением задачи — тогда null, уведомление
    /// уйдёт без клик-через (RelatedEntityKind не проставится, см. consumer'ы).</summary>
    private async Task<Guid?> ResolveMedkitIdAsync(Guid? medicationId, CancellationToken ct)
    {
        if (medicationId is null) return null;
        var medkitId = await db.Set<Medication>().AsNoTracking()
            .Where(m => m.Id == medicationId.Value).Select(m => (Guid?)m.MedkitId).FirstOrDefaultAsync(ct);
        return medkitId;
    }

    /// <summary>
    /// OCR по фото упаковки иногда искажает название препарата ("Сумматрептан" вместо
    /// "Суматриптан") — без коррекции неверное имя навсегда оседало бы как DisplayName/
    /// NormalizedName записи справочника. Суммаризатор может предложить исправление
    /// (MedicationSummary.CorrectedName) на основе цитируемых источников; здесь эта коррекция
    /// дополнительно проверяется на схожесть с исходным именем (тот же порог, что и общий
    /// нечёткий поиск) — модель могла спутать похожий, но другой препарат, а не просто увидеть
    /// опечатку. Исходное имя сохраняется алиасом на новой записи: следующее распознавание той же
    /// опечатки найдёт её сразу, без повторного внешнего запроса.
    /// </summary>
    private (string NormalizedName, string DisplayName, IReadOnlyList<string>? ExtraAliases) ResolveCorrectedName(
        MedicationEnrichmentJob job, MedicationSummary summary)
    {
        var resolution = MedicationNameCorrection.Resolve(job.NormalizedName, job.SourceDisplayName, summary);
        switch (resolution.Outcome)
        {
            case MedicationNameCorrectionOutcome.RejectedLowSimilarity:
                logger.LogWarning(
                    "MedicationEnrichmentJob {JobId}: модель предложила «{Corrected}» вместо «{Original}», " +
                    "но схожесть {Similarity:F2} слишком низкая — похоже на другой препарат, коррекция отклонена.",
                    job.Id, resolution.CorrectedName, job.SourceDisplayName, resolution.Similarity);
                break;
            case MedicationNameCorrectionOutcome.Corrected:
                logger.LogInformation(
                    "MedicationEnrichmentJob {JobId}: название исправлено «{Original}» → «{Corrected}» (схожесть {Similarity:F2}).",
                    job.Id, job.SourceDisplayName, resolution.CorrectedName, resolution.Similarity);
                break;
        }

        return (resolution.NormalizedName, resolution.DisplayName, resolution.ExtraAliases);
    }
}
