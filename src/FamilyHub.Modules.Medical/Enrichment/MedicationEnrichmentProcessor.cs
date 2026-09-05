using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
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
    MedicationSummarizer summarizer,
    KbWriter kbWriter,
    EnrichmentTrustedDomainService trustedDomains,
    ILegitimacyGuardService legitimacyGuard,
    IOptions<EnrichmentOptions> options,
    IDomainEventPublisher publisher,
    ILogger<MedicationEnrichmentProcessor> logger)
{
    /// <summary>Тот же порог, что и pg_trgm.similarity_threshold (см. RussianTextSearcher/KbLookupService) —
    /// исправленное название должно быть очевидной опечаткой исходного, а не другим препаратом.</summary>
    private const double MinCorrectionSimilarity = 0.3;

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
                job.Status = EnrichmentJobStatus.Failed;
                job.Error = guardResult.Reason;
                job.CompletedAt = DateTime.UtcNow;
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
            }
            else
            {
                rawSnippets = await provider.SearchAsync(job.NormalizedName, WebSearchTopic.Medication, ct: ct);
                if (provider.Name != "Null")
                {
                    // Считаем реальным внешним запросом только настоящих провайдеров — Null ничего
                    // никуда не отправляет, засчитывать его в месячную квоту/кулдаун незачем.
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
                job.CompletedAt = DateTime.UtcNow;
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
            await publisher.PublishAsync(new MedicationEnrichedEvent(
                job.Id, writeResult.KbId!.Value, finalDisplayName, job.RequestedByUserId, job.FamilyId), ct);
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
            }
            await db.SaveChangesAsync(ct);
            logger.LogError(ex, "MedicationEnrichmentJob {JobId} упал на попытке {Attempts} — Hangfire повторит.", job.Id, job.Attempts);
            throw;
        }
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
        var correctedName = summary.CorrectedName?.Trim();
        if (string.IsNullOrEmpty(correctedName)) return (job.NormalizedName, job.SourceDisplayName, null);

        var correctedNormalized = MedicationNameNormalizer.Normalize(correctedName);
        if (correctedNormalized.Length == 0 || correctedNormalized == job.NormalizedName)
            return (job.NormalizedName, job.SourceDisplayName, null);

        var similarity = TrigramSimilarity.Similarity(correctedNormalized, job.NormalizedName);
        if (similarity < MinCorrectionSimilarity)
        {
            logger.LogWarning(
                "MedicationEnrichmentJob {JobId}: модель предложила «{Corrected}» вместо «{Original}», " +
                "но схожесть {Similarity:F2} слишком низкая — похоже на другой препарат, коррекция отклонена.",
                job.Id, correctedName, job.SourceDisplayName, similarity);
            return (job.NormalizedName, job.SourceDisplayName, null);
        }

        logger.LogInformation(
            "MedicationEnrichmentJob {JobId}: название исправлено «{Original}» → «{Corrected}» (схожесть {Similarity:F2}).",
            job.Id, job.SourceDisplayName, correctedName, similarity);
        return (correctedNormalized, correctedName, [job.NormalizedName]);
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
