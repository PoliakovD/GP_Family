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
/// (LabAnalyteNameCleaner/LabAnalyteNormalizer.NormalizeAnalyteKey — в т.ч. кросс-алфавитная
/// свёртка "Adenovirus"/"аденовирус" в один ключ, см. план "миграция AnalyteKey") и резолвинга
/// источника (SpecimenResolver) —
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
///    источник — DisplayName как есть). Схлопнувшиеся на новом ключе строки одной записи БЕЗ
///    реального значения (ValueRaw пуст — фантомный OCR-дубль вроде "1. Гемоглобин"/"Гемоглобин")
///    сливаются как раньше; строки С реальным значением — РАЗВОДЯТСЯ суффиксом
///    (AnalyteKeyDisambiguator, §6 плана уточнения родовых названий), а не удаляются, даже если их
///    значения текстуально совпадают ("не выявлено" у обоих) — это могут быть два реальных разных
///    измерения под одинаковым родовым именем бланка (посев на сальмонеллы/на стафилококк).
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
            var renormalized = LabAnalyteNormalizer.NormalizeAnalyteKey(row.NormalizedName);
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

        // Группа на (запись, новый ключ, источник), не единственный "победитель" сразу — коллизия
        // после пересчёта ключа не всегда значит "один и тот же показатель продублирован": для
        // записей, распознанных ДО AnalyteSubjectResolver/AnalyteKeyDisambiguator (§6 плана), это
        // могут быть ДВА РЕАЛЬНЫХ разных измерения с одинаковым родовым именем на бланке (посев на
        // сальмонеллы и посев на стафилококк, оба — "Бактериальные микроорганизмы"). Порядок
        // групп — Position, тот же приоритет, что раньше отдавал "победителя" при слиянии.
        var groups = new Dictionary<(Guid MedicalRecordId, string AnalyteKey, Guid SpecimenKbId), List<LabIndicator>>();

        foreach (var indicator in all.OrderBy(i => i.Position))
        {
            // RawDisplayName — исходный текст бланка (§1.3), если он есть; для показателей,
            // распознанных ДО этой пересборки, поля ещё нет — тогда лучшее доступное приближение
            // к бланку — сам DisplayName (мог быть каноническим из KB, но это не хуже прежнего
            // состояния, а после нового прохода Clean он всё равно только чище).
            var rawSource = indicator.RawDisplayName ?? indicator.DisplayName;
            var newAnalyteKey = LabAnalyteNormalizer.NormalizeAnalyteKey(rawSource);
            if (newAnalyteKey.Length == 0) newAnalyteKey = indicator.AnalyteKey; // защитно

            var newDisplayName = LabAnalyteNameCleaner.Clean(rawSource);
            var newRawDisplayName = string.Equals(newDisplayName, rawSource, StringComparison.Ordinal) ? null : rawSource;

            indicator.AnalyteKey = newAnalyteKey;
            indicator.DisplayName = newDisplayName;
            indicator.RawDisplayName = newRawDisplayName;

            var groupKey = (indicator.MedicalRecordId, newAnalyteKey, indicator.SpecimenKbId);
            if (!groups.TryGetValue(groupKey, out var group))
            {
                group = [];
                groups[groupKey] = group;
            }
            group.Add(indicator);
        }

        var toDelete = new List<LabIndicator>();
        foreach (var group in groups.Values)
        {
            if (group.Count == 1) { run.IndicatorsUpdated++; continue; }

            // Сигнал "это OCR-дубль одной и той же строки, не два разных измерения" — ПУСТОЕ
            // значение, не совпадение/расхождение текста: реальные разные измерения под одинаковым
            // родовым именем бланка (§6 плана — посев на сальмонеллы vs на стафилококк) сплошь и
            // рядом печатают ТЕКСТУАЛЬНО ОДИНАКОВЫЙ результат ("не выявлено" у обоих) — сравнивать
            // тексты значений между собой означало бы схлопнуть именно тот случай, ради которого
            // весь этот план и затевался. Пустое ValueRaw, наоборот, однозначно значит "у этой
            // строки никогда не было собственных данных" (старый "грязный"-ключевой дубль вроде
            // "1. Гемоглобин" рядом с фантомной "Гемоглобин" без значения) — такие всегда лишние.
            var withValue = group.Where(i => !string.IsNullOrWhiteSpace(i.ValueRaw)).ToList();
            var empty = group.Where(i => string.IsNullOrWhiteSpace(i.ValueRaw)).ToList();

            if (withValue.Count == 0)
            {
                // Ни у одного нет реальных данных — сохранять нечего, оставляем один экземпляр
                // (меньший Position, как раньше отдавал приоритет победителю при слиянии).
                var keep = empty.OrderBy(i => i.Position).First();
                toDelete.AddRange(empty.Where(i => i != keep));
                run.IndicatorsMerged += empty.Count - 1;
                continue;
            }

            if (empty.Count > 0)
            {
                toDelete.AddRange(empty);
                run.IndicatorsMerged += empty.Count;
            }

            if (withValue.Count == 1) { run.IndicatorsUpdated++; continue; }

            // 2+ показателя с РЕАЛЬНЫМ значением схлопнулись на одном ключе — никогда не удаляем ни
            // один из них (даже если тексты значений совпадают дословно, см. комментарий выше) —
            // только разводим ключ тем же AnalyteKeyDisambiguator, что использует
            // MedicalDocumentExtractionProcessor при первичном сохранении. FileGroupId разведения
            // здесь — Id самого показателя (эквивалент "своя группа на строку": в отличие от
            // процессора извлечения, у уже сохранённых LabIndicator нет ссылки на исходный файл, но
            // каждая строка и так обязана остаться отдельной).
            var candidates = withValue.Select(i => new AnalyteKeyDisambiguator.Candidate(i.AnalyteKey, i.Id)).ToList();
            var disambiguation = AnalyteKeyDisambiguator.Disambiguate(candidates);
            foreach (var indicator in withValue)
            {
                if (!disambiguation.TryGetValue((indicator.AnalyteKey, indicator.Id), out var d)) continue;
                var rawHint = indicator.RawDisplayName ?? indicator.DisplayName; // до суффикса
                indicator.AnalyteKey = d.AnalyteKey;
                indicator.DisplayName += d.DisplaySuffix;
                indicator.RawDisplayName = rawHint + d.DisplaySuffix;
            }
            run.IndicatorsMerged += withValue.Count - 1;
        }

        foreach (var d in toDelete) db.LabIndicators.Remove(d);
        await db.SaveChangesAsync(ct);
    }

    // --- Этап 3: очистка справочника ---

    private async Task ClearCatalogAsync(KbRebuildRun run, CancellationToken ct)
    {
        // Строки с ручной правкой (LockedFields непусто, см. AdminCatalogService/LabAnalyteKbWriter)
        // переживают пересборку — админ уже подтвердил их содержание, обычное автообогащение и так
        // их не трогает; безусловный DELETE стирал бы эту работу без возможности отличить её от
        // строк, наполненных только конвейером. LockedFields вне EF-модели (Postgres text[], см.
        // GlobalLabAnalyteKbConfiguration) — членство читается raw SQL, тем же приёмом, что AdminCatalogService.
        var lockedIds = await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" FROM kb.global_lab_analytes_kb WHERE cardinality("LockedFields") > 0
            """).ToListAsync(ct);
        var lockedSet = lockedIds.ToHashSet();

        // Обнулить связи ДО удаления справочника — только для показателей, чья KB-строка реально
        // будет удалена. Показатель, указывающий на залоченную (сохраняемую) строку, не должен
        // потерять ссылку — она остаётся валидной и после пересборки.
        await db.LabIndicators
            .Where(i => i.KbAnalyteId != null && !lockedSet.Contains(i.KbAnalyteId!.Value))
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.KbAnalyteId, (Guid?)null)
                .SetProperty(i => i.RefSource, i =>
                    i.RefSource == RefSource.KbFixed || i.RefSource == RefSource.KbCalculated ? RefSource.None : i.RefSource),
                ct);

        run.CatalogDeleted = await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM kb.global_lab_analytes_kb WHERE cardinality(\"LockedFields\") = 0", ct);
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
            await enrichmentRequest.RequestAsync(
                analyteKey, specimenKbId, displayName, labIndicatorId: null, SystemUserId,
                force: true, origin: EnrichmentRequestOrigin.SystemMaintenance, ct: ct);
            run.ReseedRequested++;
        }

        await db.SaveChangesAsync(ct);
    }
}
