using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Enrichment;
using FamilyHub.Modules.Medical.Kb;
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
    LabAnalyteKbSummarizer summarizer,
    LabAnalyteKbWriter kbWriter,
    EnrichmentTrustedDomainService trustedDomains,
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
            if (cached is not null && cached.IsFresh)
            {
                rawSnippets = cached.Snippets;
                overrides = cached.Overrides;
                job.Provider = cached.Provider;
                logger.LogInformation(
                    "LabAnalyteEnrichmentJob {JobId}: «{Name}» — использованы закэшированные результаты поиска " +
                    "от {LastUpdatedAt:dd.MM.yyyy}, платный запрос не потребовался.",
                    job.Id, job.NormalizedName, cached.LastUpdatedAt);
            }
            else
            {
                // Отображаемое имя источника для текста поискового запроса (AnalyteSearchQueryBuilder) —
                // читается по факту непосредственно перед платным вызовом, не заранее: на кэш-хите
                // выше этот запрос вообще не нужен.
                var specimenDisplayName = await db.GlobalSpecimensKb.AsNoTracking()
                    .Where(s => s.Id == job.SpecimenKbId).Select(s => s.DisplayName).FirstOrDefaultAsync(ct);
                rawSnippets = await provider.SearchAsync(job.NormalizedName, WebSearchTopic.LabAnalyte, specimenDisplayName, ct);
                if (provider.Name != "Null")
                {
                    job.ExternalSearchAt = DateTime.UtcNow;
                    job.Provider = provider.Name;
                    await db.SaveChangesAsync(ct);
                    // Платная квота уже потрачена независимо от исхода суммаризации ниже — кэшируем
                    // ВСЕ сниппеты (не только доверенные — пересборка enrich-пайплайна) сразу после
                    // запроса, а не после успешной записи в справочник.
                    await searchCache.RecordSearchAsync(job.NormalizedName, job.SpecimenKbId, provider.Name, rawSnippets, ct);
                }
            }

            // Фильтрация по доверенным доменам (БД-список, управляемый через админку, см.
            // EnrichmentTrustedDomainService) + точечные override'ы конкретных URL — переехала сюда
            // с провайдера. Сортировка по приоритету домена ДО суммаризатора — приоритетный
            // источник должен попасть в контекст первым и не срезаться лимитом MaxSnippets (порядок
            // в БД значим, см. ReferenceRangeMerger).
            var trustedDomainsByPriority = await trustedDomains.GetActiveDomainsByPriorityAsync(WebSearchTopic.LabAnalyte, ct);
            var sortedSnippets = EnrichmentSnippetFilter.SelectEnabled(rawSnippets, trustedDomainsByPriority, overrides)
                .OrderBy(s => DomainRank(s.Url, trustedDomainsByPriority))
                .Take(options.Value.MaxSnippets)
                .ToList();

            var summarized = await summarizer.SummarizeAsync(job.SourceDisplayName, sortedSnippets, ct);
            if (!summarized.Success || summarized.Summary is null)
            {
                job.Status = EnrichmentJobStatus.Failed;
                job.Error = summarized.Error;
                job.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            var mergedRanges = ReferenceRangeMerger.Merge(summarized.Summary.RefRanges, sortedSnippets, trustedDomainsByPriority);
            var summary = summarized.Summary with { RefRanges = mergedRanges };

            var source = BuildSourceLabel(provider.Name, sortedSnippets, summary.UsedSourceIndexes);

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var writeResult = await kbWriter.UpsertAsync(job.NormalizedName, job.SpecimenKbId, job.SourceDisplayName, summary, source, ct);
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
            if (job.Attempts >= MaxAttempts)
            {
                job.Status = EnrichmentJobStatus.Failed;
                job.CompletedAt = DateTime.UtcNow;
            }
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

    /// <summary>Индекс домена в trustedDomainsByPriority — та же логика ранжирования, что
    /// ReferenceRangeMerger.ResolveRank, но здесь применяется к порядку СНИППЕТОВ перед тем, как их
    /// увидит модель (см. class doc: приоритетный источник не должен срезаться MaxSnippets).</summary>
    private static int DomainRank(string url, IReadOnlyList<string> trustedDomainsByPriority)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return trustedDomainsByPriority.Count;

        var host = uri.Host;
        for (var i = 0; i < trustedDomainsByPriority.Count; i++)
        {
            var trusted = trustedDomainsByPriority[i];
            if (host.Equals(trusted, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + trusted, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return trustedDomainsByPriority.Count;
    }
}
