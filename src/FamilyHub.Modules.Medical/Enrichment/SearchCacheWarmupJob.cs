using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.Kb;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Enrichment;

/// <summary>
/// Прогрев кэша веб-поиска (см. class doc SearchWarmupRun) — на каждое имя делает ТОЛЬКО платный
/// поиск и запись в кэш (provider.SearchAsync → *SearchCacheService.RecordSearchAsync), без единого
/// обращения к локальной LLM: ни LegitimacyGuardService, ни суммаризация, ни запись в
/// kb.global_*_kb. Справочник наполнится позже бесплатно — обычный конвейер обогащения
/// (LabAnalyteEnrichmentProcessor/MedicationEnrichmentProcessor) на живом пользовательском спросе
/// найдёт уже свежую строку кэша и не пойдёт во внешний поиск повторно.
///
/// Батч + самопродолжение (BatchSize, тот же приём, что LabAnalyteKbReenrichJob) — единственный
/// воркер очереди "enrichment" не монополизируется на весь прогон, задачи живых пользователей
/// вклиниваются между батчами. AutomaticRetry=0 — прогресс живёт в самой строке SearchWarmupRun
/// (Cursor), Hangfire-ретрай на инфраструктурном сбое не нужен: следующий ручной/самопродолженный
/// вызов и так продолжит с сохранённого курсора; единичный сбой ПОИСКА по одному имени не бросает
/// исключение вообще (см. ниже), поэтому ретраить в привычном смысле нечего.
/// </summary>
[Queue("enrichment")]
[AutomaticRetry(Attempts = 0)]
public class SearchCacheWarmupJob(
    AppDbContext db,
    IMedicationSearchProvider provider,
    MedicationSearchCacheService medicationCache,
    LabAnalyteSearchCacheService analyteCache,
    KbLookupService medicationKbLookup,
    LabAnalyteKbLookupService analyteKbLookup,
    IWebSearchValveService searchValve,
    IBackgroundJobClient backgroundJobs,
    ILogger<SearchCacheWarmupJob> logger)
{
    public const int BatchSize = 10;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task RunAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await db.SearchWarmupRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            logger.LogWarning("SearchWarmupRun {RunId} не найден — пропускаем.", runId);
            return;
        }

        if (run.Status is SearchWarmupStatus.Completed or SearchWarmupStatus.Failed or SearchWarmupStatus.Cancelled)
            return; // финальный статус уже проставлен предыдущим батчем — самопродолжение опоздало

        var names = JsonSerializer.Deserialize<List<WarmupName>>(run.NamesJson, JsonOptions) ?? [];
        string? specimenDisplayName = null;
        if (run.Topic == WebSearchTopic.LabAnalyte && run.SpecimenKbId is { } specimenId)
        {
            specimenDisplayName = await db.GlobalSpecimensKb.AsNoTracking()
                .Where(s => s.Id == specimenId).Select(s => s.DisplayName).FirstOrDefaultAsync(ct);
        }

        try
        {
            var processedInBatch = 0;
            while (run.Cursor < names.Count && processedInBatch < BatchSize)
            {
                if (run.CancelRequested)
                {
                    run.Status = SearchWarmupStatus.Cancelled;
                    run.FinishedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation("SearchWarmupRun {RunId}: остановлен вручную на {Cursor}/{Total}.",
                        run.Id, run.Cursor, run.TotalNames);
                    return;
                }

                if (run.MaxPaidCalls is { } budget && run.PaidCalls >= budget)
                {
                    run.Status = SearchWarmupStatus.Completed;
                    run.FinishedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation("SearchWarmupRun {RunId}: бюджет {Budget} платных вызовов исчерпан, прогон завершён.",
                        run.Id, budget);
                    return;
                }

                // Вентиль платного поиска (ADR-0005 §9) — прогон не отменяется, а паркуется:
                // курсор/счётчики остаются как есть, DeferredEnrichmentReleaseJob (при открытии
                // вентиля) сам возобновит его энкью на SearchCacheWarmupJob.RunAsync(run.Id).
                if (provider.Name != "Null" && await searchValve.IsPausedAsync(ct))
                {
                    run.Status = SearchWarmupStatus.Paused;
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation("SearchWarmupRun {RunId}: приостановлен на {Cursor}/{Total} — вентиль платного поиска закрыт.",
                        run.Id, run.Cursor, run.TotalNames);
                    return;
                }

                var name = names[run.Cursor];
                await ProcessOneAsync(run, name, specimenDisplayName, ct);
                run.Cursor++;
                processedInBatch++;
            }

            if (run.Cursor >= names.Count)
            {
                run.Status = SearchWarmupStatus.Completed;
                run.FinishedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                logger.LogInformation(
                    "SearchWarmupRun {RunId}: завершён — платных вызовов {PaidCalls}, пропущено (уже в KB) {SkippedKbHit}, " +
                    "пропущено (свежий кэш) {SkippedFreshCache}, ошибок {Failures}.",
                    run.Id, run.PaidCalls, run.SkippedKbHit, run.SkippedFreshCache, run.Failures);
                return;
            }

            await db.SaveChangesAsync(ct);
            backgroundJobs.Enqueue<SearchCacheWarmupJob>(j => j.RunAsync(run.Id, CancellationToken.None));
        }
        catch (Exception ex)
        {
            run.Status = SearchWarmupStatus.Failed;
            run.LastError = ex.Message;
            run.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogError(ex, "SearchWarmupRun {RunId} упал на курсоре {Cursor} — прогон остановлен.", run.Id, run.Cursor);
            // Не throw — Hangfire-ретрай (AutomaticRetry=0 и так его отключает) здесь бесполезен:
            // прогон уже переведён в терминальный Failed, повторный запуск — явное действие админа.
        }
    }

    private async Task ProcessOneAsync(
        SearchWarmupRun run, WarmupName name, string? specimenDisplayName, CancellationToken ct)
    {
        // Промах/попадание в справочник — уже имеющееся пропускаем молча (решение владельца):
        // прогревать кэш ради названия, у которого и так есть готовая статья, незачем.
        var kbHit = run.Topic == WebSearchTopic.LabAnalyte
            ? (await analyteKbLookup.LookupAsync(name.Normalized, run.SpecimenKbId!.Value, ct)).Kind == KbLookupKind.Hit
            : (await medicationKbLookup.LookupAsync(name.Normalized, ct)).Kind == KbLookupKind.Hit;
        if (kbHit)
        {
            run.SkippedKbHit++;
            return;
        }

        // Уже свежий кэш — новый платный вызов ничего бы не добавил.
        var fresh = run.Topic == WebSearchTopic.LabAnalyte
            ? (await analyteCache.GetCachedAsync(name.Normalized, run.SpecimenKbId!.Value, ct))?.IsFresh == true
            : (await medicationCache.GetCachedAsync(name.Normalized, ct))?.IsFresh == true;
        if (fresh)
        {
            run.SkippedFreshCache++;
            return;
        }

        // Null-провайдер (Enrichment:Provider не настроен, обычно dev) — тот же приём, что во
        // всех трёх enrich-процессорах: он "молча не работает" (NullMedicationSearchProvider),
        // здесь это значит вообще не писать кэш и не считать это платным вызовом — иначе строка
        // с CanBeUpdatedAfter=+1 месяц заблокировала бы реальный поиск на весь кулдаун, как только
        // провайдер позже сконфигурируют по-настоящему.
        if (provider.Name == "Null") return;

        try
        {
            var callContext = new WebSearchCallContext("SearchCacheWarmup", run.Id);
            var snippets = await provider.SearchAsync(name.Normalized, run.Topic, specimenDisplayName, ct, callContext);

            if (run.Topic == WebSearchTopic.LabAnalyte)
                await analyteCache.RecordSearchAsync(name.Normalized, run.SpecimenKbId!.Value, provider.Name, snippets, ct);
            else
                await medicationCache.RecordSearchAsync(name.Normalized, provider.Name, snippets, ct);

            run.PaidCalls++;
        }
        catch (Exception ex)
        {
            // Единичный сбой поиска (сеть, HTTP-ошибка провайдера) не должен ронять весь прогон —
            // WebSearchCallLogger уже записал строку аудита с самой ошибкой (провайдер логирует
            // сам внутри SearchAsync), здесь только счётчик для итогового отчёта админу.
            run.Failures++;
            logger.LogWarning(ex, "SearchWarmupRun {RunId}: сбой платного поиска «{Name}», продолжаем со следующего имени.",
                run.Id, name.Raw);
        }
    }
}
