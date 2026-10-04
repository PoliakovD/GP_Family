using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Enrichment;
using FamilyHub.Modules.Medical.Kb;
using FamilyHub.Modules.Medical.Pipeline;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Шаги обогащения справочника показателей (ветка medicalrecords, пересборка enrich-пайплайна
/// анализов) — повторная проверка справочника → кэш сниппетов (LabAnalyteSearchCacheService,
/// зеркало MedicationSearchCacheService — закрывает ранее задокументированный пропуск) → веб-поиск
/// по лабораторным источникам, отсортированным по приоритету (EnrichmentTrustedDomain, Topic=LabAnalyte)
/// → суммаризация локальным Qwen → детерминированный merge норм по приоритету источника
/// (ReferenceRangeMerger) → запись. Без шага коррекции названия (CorrectedName), в отличие от
/// MedicationEnrichmentProcessor — имя показателя приходит из уже гейтованного LLM-извлечения
/// (LmStudioMedicalDocumentExtractor) и второго прохода OCR-коррекции (OcrNameCorrector), опечатки
/// маловероятны. Та же выделенная очередь "enrichment".
/// </summary>
[Queue("enrichment")]
[AutomaticRetry(Attempts = LabAnalyteEnrichmentProcessor.MaxAttempts, DelaysInSeconds = [60, 600, 3600])]
public class LabAnalyteEnrichmentProcessor(
    AppDbContext db,
    LabAnalyteKbLookupService kbLookup,
    LabAnalyteSearchCacheService searchCache,
    IMedicationSearchProvider provider,
    WebSearchCallLogger callLogger,
    LabAnalyteKbSummarizer summarizer,
    LabAnalyteKbWriter kbWriter,
    EnrichmentTrustedDomainService trustedDomains,
    ILegitimacyGuardService legitimacyGuard,
    IAnalytePlausibilityGuardService plausibilityGuard,
    IWebSearchValveService searchValve,
    IEnrichmentReviewConfigService reviewConfig,
    IOptions<EnrichmentOptions> options,
    IBackgroundJobClient backgroundJobs,
    ILogger<LabAnalyteEnrichmentProcessor> logger)
{
    /// <summary>Должно совпадать с Attempts в [AutomaticRetry] — см. MedicalDocumentExtractionProcessor.MaxAttempts.</summary>
    public const int MaxAttempts = 3;

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
            // Первый обязательный шаг (PipelineCatalog.LegitimacyCheckStep) — ДО любого обращения к
            // справочнику или внешнему поиску: SourceDisplayName — свободный текст (из LLM-извлечения
            // документа или ручного ввода пользователем, см. ExtractionQueryService.CreateIndicatorAsync),
            // который дальше попадёт и в поисковый запрос, и в промпт суммаризатора.
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
                await db.SaveChangesAsync(ct);
                logger.LogWarning(
                    "LabAnalyteEnrichmentJob {JobId} остановлена проверкой легитимности: {Reason}", job.Id, guardResult.Reason);
                return;
            }

            // Гейт «на бред» (class doc AnalytePlausibilityGuardService) — для всех происхождений.
            (double? Confidence, string? Reason)? plausibilityConfidence = null;
            // Страж легитимности сам по себе не отличает "Пациент" от показателя (уверенность 0.98), поэтому
            // правдоподобность проверяется для ВСЕХ происхождений: платный поиск не должен уходить по
            // слову из шапки бланка или обрезанному названию. Плюс детерминированный фильтр до модели.
            var badName = AnalyteNameQuality.RejectReason(job.SourceDisplayName);
            if (badName is not null)
            {
                job.Status = EnrichmentJobStatus.Failed;
                job.Error = badName;
                job.FailureReason = EnrichmentFailureReason.Plausibility;
                job.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            {
                var specimenDisplayName = await db.GlobalSpecimensKb.AsNoTracking()
                    .Where(s => s.Id == job.SpecimenKbId).Select(s => s.DisplayName).FirstOrDefaultAsync(ct);
                var plausibility = await plausibilityGuard.CheckAsync(job.SourceDisplayName, specimenDisplayName, ct);
                if (!plausibility.IsPlausible)
                {
                    if (plausibility.IsTransientFailure)
                        throw new LmStudioUnavailableException(plausibility.Reason ?? "Локальный сервер распознавания недоступен.");

                    job.Status = EnrichmentJobStatus.Failed;
                    job.Error = plausibility.Reason;
                    job.FailureReason = EnrichmentFailureReason.Plausibility;
                    job.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    logger.LogWarning(
                        "LabAnalyteEnrichmentJob {JobId} остановлена гейтом правдоподобности: {Reason}", job.Id, plausibility.Reason);
                    return;
                }

                plausibilityConfidence = (plausibility.Confidence, plausibility.ConfidenceReason);
            }

            // Соседняя задача (другой анализ, тот же показатель+биоматериал) могла успеть наполнить
            // справочник, пока эта ждала своей очереди. Force (LabAnalyteKbReenrichJob) намеренно
            // пропускает этот выход — цель форсированной задачи ИМЕННО в том, чтобы пройти пайплайн
            // заново поверх уже существующей записи, а не подтвердить, что она есть.
            var existing = await kbLookup.LookupAsync(job.NormalizedName, job.SpecimenKbId, ct);
            if (!job.Force && existing.Kind == KbLookupKind.Hit)
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

            // Настоящий кэш, не просто лог "когда можно/нельзя" — тот же приём, что
            // MedicationEnrichmentProcessor: переиспользуем сохранённые сниппеты, если минимальный
            // интервал обновления ещё не истёк, суммаризацию можно пересчитывать сколько угодно раз
            // (например, при доработке промпта), не тратя платный запрос на одно и то же снова.
            var cached = provider.Name != "Null" ? await searchCache.GetCachedAsync(job.NormalizedName, job.SpecimenKbId, ct) : null;

            IReadOnlyList<WebSnippet> rawSnippets;
            IReadOnlyDictionary<string, bool>? overrides = null;
            // Слово биоматериала в поисковом запросе — текст группы поиска (ADR-0018: кровь/венозная кровь/плазма
            // → один запрос «… (кровь)»), у биоматериала без группы — его собственное название.
            var specimenDisplayNameForLog = (await searchCache.GetSearchGroupAsync(job.SpecimenKbId, ct)).QueryLabel;

            // «Пробел по единице» (Force из LabAnalyteEnrichmentRequestService): если сохранённая выдача —
            // даже устаревшая — уже содержит нормы в нужной (последней в списке) единице, платный повтор
            // не нужен: пересуммаризируем кэш (LabAnalyteCacheUnitsBackfillJob заполняет Units старым строкам).
            var gapUnit = job.Units?.Split(';', StringSplitOptions.TrimEntries).LastOrDefault();
            if (cached is not null && (cached.IsFresh || (job.Force && cached.CoversUnit(gapUnit))))
            {
                rawSnippets = cached.Snippets;
                overrides = cached.Overrides;
                job.Provider = cached.Provider;
                logger.LogInformation(
                    "LabAnalyteEnrichmentJob {JobId}: «{Name}» — использованы закэшированные результаты поиска " +
                    "от {LastUpdatedAt:dd.MM.yyyy}, платный запрос не потребовался.",
                    job.Id, job.NormalizedName, cached.LastUpdatedAt);
                // Кэш-хит — не платный вызов, но всё равно строка в аудит-логе (см. class doc
                // WebSearchCallOutcome.CacheHit): без неё нельзя ответить на вопрос "работает ли
                // кэш" по одной этой таблице.
                await callLogger.LogAsync(new WebSearchCallLogEntry(
                    cached.Provider, WebSearchTopic.LabAnalyte, job.NormalizedName, specimenDisplayNameForLog,
                    string.Empty, null, null, 0, WebSearchCallOutcome.CacheHit, cached.Snippets.Count, null, null,
                    "LabAnalyteEnrichment", job.Id), ct);
            }
            else
            {
                // Проверки платной ветки — ТОЛЬКО на реальном платном вызове, не на кэш-хите выше:
                // кэш ничего не стоит независимо от вентиля и одобрения. Null-провайдер не гейтим
                // вовсе — он никуда не ходит, иначе в dev/тестах задачи парковались бы навсегда
                // без реального провайдера.
                if (provider.Name != "Null")
                {
                    // Гейт 1 (ADR-0018): каждый платный поиск ждёт ручного одобрения админа.
                    // Уверенность этапа запроса — страж легитимности, а для ручного ввода ещё и
                    // страж правдоподобности: берём минимум (любая отсутствующая → null → «ниже порога»).
                    var (queryConfidence, queryReason) = EnrichmentReviewGate.CombineQueryConfidence(
                        (guardResult.Confidence, guardResult.ConfidenceReason), plausibilityConfidence);
                    if (EnrichmentReviewGate.TryParkForSearchApproval(job, queryConfidence, queryReason))
                    {
                        await db.SaveChangesAsync(ct); // НЕ CompletedAt: ждёт админа; return без исключения — как Deferred
                        logger.LogInformation(
                            "LabAnalyteEnrichmentJob {JobId}: платный поиск ждёт одобрения админа (уверенность {Confidence}).",
                            job.Id, job.QueryConfidence);
                        return;
                    }

                    // Вентиль платного поиска (ADR-0005 §9, замена месячной квоты) — поверх
                    // одобрения: одобренная задача при закрытом вентиле уходит в Deferred.
                    if (await searchValve.IsPausedAsync(ct))
                    {
                        job.Status = EnrichmentJobStatus.Deferred;
                        job.Error = "Платный веб-поиск на паузе — задача отложена до его включения.";
                        await db.SaveChangesAsync(ct); // НЕ CompletedAt: задача не завершена, а отложена
                        logger.LogInformation("LabAnalyteEnrichmentJob {JobId}: отложена — вентиль платного поиска закрыт.", job.Id);
                        return;
                    }
                }

                // Отображаемое имя источника для текста поискового запроса (AnalyteSearchQueryBuilder) —
                // читается по факту непосредственно перед платным вызовом, не заранее: на кэш-хите
                // выше этот запрос вообще не нужен.
                var callContext = new WebSearchCallContext("LabAnalyteEnrichment", job.Id);
                // Текст запроса — правка админа (ProposedQueryText) либо нормализованное имя.
                rawSnippets = await provider.SearchAsync(
                    EnrichmentReviewGate.EffectiveQuery(job), WebSearchTopic.LabAnalyte, specimenDisplayNameForLog, ct, callContext);
                if (provider.Name != "Null")
                {
                    job.ExternalSearchAt = DateTime.UtcNow;
                    job.Provider = provider.Name;
                    await db.SaveChangesAsync(ct);
                    // Платная квота уже потрачена независимо от исхода суммаризации ниже — кэшируем
                    // ВСЕ сниппеты (не только доверенные — пересборка enrich-пайплайна) сразу после
                    // запроса, а не после успешной записи в справочник.
                    await searchCache.RecordSearchAsync(
                        job.NormalizedName, job.SpecimenKbId, provider.Name, rawSnippets, ct, job.Units, job.SourceDisplayName);
                }
            }

            // Фильтрация по доверенным доменам (БД-список, управляемый через админку, см.
            // EnrichmentTrustedDomainService) + точечные override'ы конкретных URL — переехала сюда
            // с провайдера. Сортировка по приоритету домена ДО суммаризатора — приоритетный
            // источник должен попасть в контекст первым и не срезаться лимитом MaxSnippets (порядок
            // в БД значим, см. ReferenceRangeMerger).
            var trustedDomainsByPriority = await trustedDomains.GetActiveDomainsByPriorityAsync(WebSearchTopic.LabAnalyte, ct);
            var sortedSnippets = EnrichmentSnippetFilter.SelectForSummary(
                rawSnippets, trustedDomainsByPriority, overrides, options.Value.MaxSnippets, rankOrder: true);

            // Пустой результат фильтрации — самый частый и самый дешёвый в починке отказ (см.
            // «Требует внимания» в админке): всё, что вернул поиск, отбросил домен-фильтр.
            // Проверяем ДО суммаризатора, а не полагаемся на его собственную проверку
            // (LabAnalyteKbSummarizer.SummarizeAsync тоже отказывает на пустом списке) — так
            // единственный воркер очереди enrichment не тратится на локальный вызов LLM, заведомо
            // обречённый на тот же отказ.
            if (sortedSnippets.Count == 0)
            {
                job.Status = EnrichmentJobStatus.Failed;
                job.Error = "Нет сниппетов от доверенных источников — суммаризировать нечего.";
                job.FailureReason = EnrichmentFailureReason.NoTrustedSnippets;
                job.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            var summarized = await summarizer.SummarizeAsync(job.SourceDisplayName, sortedSnippets, ct, job.Units);
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

            var mergedRanges = ReferenceRangeMerger.Merge(summarized.Summary.RefRanges, sortedSnippets, trustedDomainsByPriority);
            var summary = summarized.Summary with { RefRanges = mergedRanges };

            var source = EnrichmentReviewGate.BuildSourceLabel(provider.Name, sortedSnippets, summary.UsedSourceIndexes);

            // Гейт 2 (ADR-0018): уверенность ниже порога (или не вернулась) — в kb НЕ пишем,
            // черновик ждёт ревью админа; LabAnalyteKbWriter и RecalculateIndicatorFlagsJob не вызываются.
            var thresholds = await reviewConfig.GetAsync(ct);
            if (EnrichmentReviewGate.NeedsResultReview(
                    summarized.Confidence, thresholds.ResultMin(EnrichmentReviewDomain.Analyte)))
            {
                var draft = new LabAnalyteDraft(
                    job.NormalizedName, job.SpecimenKbId, job.SourceDisplayName, source, summary,
                    EnrichmentDraftSerializer.ToDraftSnippets(sortedSnippets), summarized.FieldSources);
                EnrichmentReviewGate.ParkForResultReview(
                    job, EnrichmentDraftSerializer.Serialize(draft), summarized.Confidence, summarized.ConfidenceReason);
                await db.SaveChangesAsync(ct);
                logger.LogInformation(
                    "LabAnalyteEnrichmentJob {JobId}: уверенность результата {Confidence} ниже порога — черновик ждёт ревью админа.",
                    job.Id, summarized.Confidence);
                return;
            }

            job.ResultConfidence = summarized.Confidence;
            job.ResultConfidenceReason = summarized.ConfidenceReason;

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var writeResult = await kbWriter.UpsertAsync(job.NormalizedName, job.SpecimenKbId, job.SourceDisplayName, summary, source, ct);
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
            logger.LogError(ex, "LabAnalyteEnrichmentJob {JobId} упал на попытке {Attempts} — Hangfire повторит.", job.Id, job.Attempts);
            throw;
        }
    }
}
