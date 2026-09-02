using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Пересборка справочника лабораторных показателей поверх исправленного кода очистки имён
/// (LabAnalyteNameCleaner/LabAnalyteNormalizer) и резолвинга источника (SpecimenResolver) —
/// применяет их к уже накопленным "грязным" данным задним числом (нумерация пункта бланка в
/// AnalyteKey, КАПС в DisplayName и т.п.), не только к новым распознаваниям. Запускается вручную
/// из админки (AdminKbRebuildService), не автоматически — в отличие от LabAnalyteKbReenrichJob
/// (та реагирует на дрейф PayloadVersion и подцепляется на каждом старте API), это разовое
/// действие после деплоя исправлений.
///
/// Четыре этапа, резюмируемых курсором в самой строке KbRebuildRun (переживает рестарт процесса —
/// тот же приём, что EncryptionRotationRun):
/// 1. Перекейовка кэша сниппетов (kb.lab_analyte_search_cache) — НЕ трогаем, если сделать это уже
///    после очистки справочника: оплаченные сниппеты нужны, чтобы пересев (шаг 4) не ушёл в
///    платный поиск заново. Только перенормализация NormalizedName — маппинг Specimen(enum)→
///    SpecimenKbId уже сделан миграцией ReworkSpecimenAsData, здесь ему взяться неоткуда.
/// 2. Пересчёт показателей — AnalyteKey/DisplayName/RawDisplayName из ИСХОДНОГО текста бланка
///    (RawDisplayName, если есть — для записей, распознанных до этой пересборки, его нет, тогда
///    источник — DisplayName как есть). Схлопнувшиеся на новом ключе строки одной записи сливаются.
/// 3. Очистка справочника — сами объяснения/нормы всё равно устарели вместе со старым
///    "грязным" ключом, пересчитывать их на месте нет смысла (LockedFields из §3 плана здесь
///    пока не проверяется — колонки ещё нет; когда появится, сюда добавится фильтр).
/// 4. Пересев — обогащение по каждой уникальной резолвленной паре (AnalyteKey, SpecimenKbId).
///    Жёсткий гейт (SpecimenKbId != Unresolved) — внутри LabAnalyteEnrichmentRequestService,
///    единственной точки входа, здесь только фильтр по нему для счётчика.
/// </summary>
[Queue("enrichment")]
[AutomaticRetry(Attempts = LabAnalyteKbRebuildJob.MaxAttempts, DelaysInSeconds = [60, 600, 3600])]
public class LabAnalyteKbRebuildJob(
    AppDbContext db,
    LabAnalyteEnrichmentRequestService enrichmentRequest,
    ILogger<LabAnalyteKbRebuildJob> logger)
{
    /// <summary>Тот же приём, что MedicalDocumentExtractionProcessor.MaxAttempts — на последней
    /// попытке catch-блок сам переводит прогон в Failed, иначе Hangfire сдаётся молча и строка
    /// осталась бы в Running навсегда, блокируя частичный уникальный индекс (Status=Running).</summary>
    public const int MaxAttempts = 3;

    /// <summary>Задача поставлена системой — тот же приём, что LabAnalyteKbReenrichJob.SystemUserId.</summary>
    private static readonly Guid SystemUserId = Guid.Empty;

    public async Task RunAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await db.KbRebuildRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            logger.LogWarning("KbRebuildRun {RunId} не найден — пропускаем.", runId);
            return;
        }

        run.Attempts++;
        await db.SaveChangesAsync(ct);

        try
        {
            if (run.StageIndex <= 0)
            {
                await RekeySearchCacheAsync(run, ct);
                run.StageIndex = 1;
                await db.SaveChangesAsync(ct);
            }
            if (run.StageIndex <= 1)
            {
                await RecalculateIndicatorsAsync(run, ct);
                run.StageIndex = 2;
                await db.SaveChangesAsync(ct);
            }
            if (run.StageIndex <= 2)
            {
                await ClearCatalogAsync(run, ct);
                run.StageIndex = 3;
                await db.SaveChangesAsync(ct);
            }
            if (run.StageIndex <= 3)
            {
                await ReseedAsync(run, ct);
                run.StageIndex = 4;
                await db.SaveChangesAsync(ct);
            }

            run.Status = KbRebuildStatus.Completed;
            run.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "KbRebuildRun {RunId}: завершён — кэш слито {CacheMerged}, показателей обновлено {IndicatorsUpdated} " +
                "(слито {IndicatorsMerged}), справочник очищен ({CatalogDeleted} строк), пересеяно {ReseedRequested} задач.",
                run.Id, run.CacheMerged, run.IndicatorsUpdated, run.IndicatorsMerged, run.CatalogDeleted, run.ReseedRequested);
        }
        catch (Exception ex)
        {
            run.LastError = ex.Message;
            if (run.Attempts >= MaxAttempts)
            {
                run.Status = KbRebuildStatus.Failed;
                run.FinishedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            logger.LogError(ex, "KbRebuildRun {RunId} упал на этапе {StageIndex}, попытка {Attempts} — Hangfire повторит с сохранённого этапа.",
                run.Id, run.StageIndex, run.Attempts);
            throw;
        }
    }

    // --- Этап 1: перекейовка кэша сниппетов ---

    private async Task RekeySearchCacheAsync(KbRebuildRun run, CancellationToken ct)
    {
        // Таблица платного кэша по масштабу проекта — единицы-десятки строк в месяц (см. ADR-0005) —
        // один проход в памяти без постраничного курсора оправдан, в отличие от
        // EncryptionRotationRun, рассчитанного на все [Encrypted]-сущности БД разом.
        var rows = await db.LabAnalyteSearchCaches.ToListAsync(ct);
        var byKey = new Dictionary<(string NormalizedName, Guid SpecimenKbId), bool>();

        // От свежих к старым — при коллизии ключа после перенормализации первой (свежей) достаётся
        // ключ, остальные (более старые дубликаты) удаляются, не наоборот.
        foreach (var row in rows.OrderByDescending(r => r.LastUpdatedAt))
        {
            var renormalized = LabAnalyteNormalizer.Normalize(row.NormalizedName);
            if (renormalized.Length == 0) renormalized = row.NormalizedName; // защитно — не должно случаться

            var key = (renormalized, row.SpecimenKbId);
            if (byKey.ContainsKey(key))
            {
                db.LabAnalyteSearchCaches.Remove(row);
                run.CacheMerged++;
                continue;
            }

            byKey[key] = true;
            row.NormalizedName = renormalized;
        }

        await db.SaveChangesAsync(ct);
    }

    // --- Этап 2: пересчёт показателей ---

    private async Task RecalculateIndicatorsAsync(KbRebuildRun run, CancellationToken ct)
    {
        var all = await db.LabIndicators.ToListAsync(ct);
        var byKey = new Dictionary<(Guid MedicalRecordId, string AnalyteKey, Guid SpecimenKbId), LabIndicator>();
        var toDelete = new List<LabIndicator>();

        foreach (var indicator in all.OrderBy(i => i.Position))
        {
            // RawDisplayName — исходный текст бланка (§1.3), если он есть; для показателей,
            // распознанных ДО этой пересборки, поля ещё нет — тогда лучшее доступное приближение
            // к бланку — сам DisplayName (мог быть каноническим из KB, но это не хуже прежнего
            // состояния, а после нового прохода Clean он всё равно только чище).
            var rawSource = indicator.RawDisplayName ?? indicator.DisplayName;
            var newAnalyteKey = LabAnalyteNormalizer.Normalize(rawSource);
            if (newAnalyteKey.Length == 0) newAnalyteKey = indicator.AnalyteKey; // защитно

            var newDisplayName = LabAnalyteNameCleaner.Clean(rawSource);
            var newRawDisplayName = string.Equals(newDisplayName, rawSource, StringComparison.Ordinal) ? null : rawSource;

            var key = (indicator.MedicalRecordId, newAnalyteKey, indicator.SpecimenKbId);
            if (byKey.TryGetValue(key, out var existing))
            {
                // Коллизия внутри одной записи — до пересчёта эти показатели различались по
                // "грязному" ключу (например, "1. Гемоглобин" и "Гемоглобин"), теперь схлопнулись.
                // Побеждает непустое значение, при равенстве — меньший Position (см. §4.2 плана).
                var keepExisting = !string.IsNullOrWhiteSpace(existing.ValueRaw)
                    || string.IsNullOrWhiteSpace(indicator.ValueRaw)
                    || existing.Position <= indicator.Position;
                var drop = keepExisting ? indicator : existing;
                if (!keepExisting)
                {
                    indicator.AnalyteKey = newAnalyteKey;
                    indicator.DisplayName = newDisplayName;
                    indicator.RawDisplayName = newRawDisplayName;
                    byKey[key] = indicator;
                }
                toDelete.Add(drop);
                run.IndicatorsMerged++;
                continue;
            }

            indicator.AnalyteKey = newAnalyteKey;
            indicator.DisplayName = newDisplayName;
            indicator.RawDisplayName = newRawDisplayName;
            byKey[key] = indicator;
            run.IndicatorsUpdated++;
        }

        foreach (var d in toDelete) db.LabIndicators.Remove(d);
        await db.SaveChangesAsync(ct);
    }

    // --- Этап 3: очистка справочника ---

    private async Task ClearCatalogAsync(KbRebuildRun run, CancellationToken ct)
    {
        // Обнулить связи ДО удаления справочника — иначе показатели временно указывали бы на уже
        // удалённую строку (не FK — БД не запретит, но следующий каскад расчёта увидел бы "чужой"
        // Id, которого больше нет).
        await db.LabIndicators.ExecuteUpdateAsync(s => s
            .SetProperty(i => i.KbAnalyteId, (Guid?)null)
            .SetProperty(i => i.RefSource, i =>
                i.RefSource == RefSource.KbFixed || i.RefSource == RefSource.KbCalculated ? RefSource.None : i.RefSource),
            ct);

        // LockedFields (§3 плана) здесь пока не проверяется — колонки ещё нет в этой пересборке;
        // когда появится, сюда добавится "WHERE cardinality(\"LockedFields\") = 0 OR \"LockedFields\" IS NULL".
        run.CatalogDeleted = await db.Database.ExecuteSqlRawAsync("DELETE FROM kb.global_lab_analytes_kb", ct);
        await db.SaveChangesAsync(ct);
    }

    // --- Этап 4: пересев ---

    private async Task ReseedAsync(KbRebuildRun run, CancellationToken ct)
    {
        var raw = await db.LabIndicators
            .Where(i => i.SpecimenKbId != SpecimenContextIds.Unresolved)
            .Select(i => new { i.AnalyteKey, i.SpecimenKbId, i.DisplayName })
            .ToListAsync(ct);

        // Одна задача на уникальную пару — несколько показателей разных записей с тем же
        // (AnalyteKey, SpecimenKbId) не должны плодить отдельные задачи (дедуп на БД-уровне внутри
        // LabAnalyteEnrichmentRequestService и так поймал бы это, но так дешевле).
        var distinctPairs = raw
            .GroupBy(x => (x.AnalyteKey, x.SpecimenKbId))
            .Select(g => (g.Key.AnalyteKey, g.Key.SpecimenKbId, DisplayName: g.First().DisplayName));

        foreach (var (analyteKey, specimenKbId, displayName) in distinctPairs)
        {
            await enrichmentRequest.RequestAsync(analyteKey, specimenKbId, displayName, labIndicatorId: null, SystemUserId, force: true, ct);
            run.ReseedRequested++;
        }

        await db.SaveChangesAsync(ct);
    }
}
