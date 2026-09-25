using System.Globalization;
using System.Text.Json;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Audit;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.MedicalRecords;
using FamilyHub.Modules.Medical.Pipeline;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using DomainLabIndicator = FamilyHub.Domain.Entities.LabIndicator;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>Failed — только у RegenerateSummaryAsync: запись/доступ в порядке, но сама
/// суммаризация не удалась (LM Studio недоступен, гейт отклонил пустой ответ и т.п.).</summary>
public enum ExtractionQueryResult { Success, NotFound, Forbidden, Failed }

/// <summary>
/// Чтение результатов конвейера извлечения (ветка medicalrecords). Показатели/статус/summary
/// наследуют видимость родительской мед-записи — своей у них нет, тот же принцип, что у вложений
/// (см. AttachmentService.GetForMedicalRecordAsync): просмотр чужой расшаренной записи пишет аудит.
/// </summary>
public class ExtractionQueryService(
    AppDbContext db, MedicalRecordService medicalRecords, Kb.KbLookupService medicationKbLookup,
    Kb.KbAnalyteCatalogService analyteCatalog, IMedicalAuditWriter audit,
    LabAnalyteKbLookupService analyteKbLookup, LabAnalyteEnrichmentRequestService enrichmentRequest,
    ILegitimacyGuardService legitimacyGuard, IAnalytePlausibilityGuardService plausibilityGuard,
    LlmQueuePositionService queuePositionService, IBackgroundJobClient backgroundJobs)
{
    /// <summary>Заметка 3 — гейты проверяются СИНХРОННО, до постановки обогащения в очередь, а не
    /// только внутри фонового LabAnalyteEnrichmentProcessor (тот же порог для ManualEntry, что уже
    /// есть там — этот вызов лишь пододвигает его раньше, чтобы отказ был виден сразу, а не терялся
    /// в Job.Status=Failed без какой-либо видимой пользователю причины). Отказ НЕ блокирует
    /// сохранение самого показателя/источника — только обогащение общего справочника.</summary>
    private async Task<string?> CheckManualEntryGatesAsync(string analyteName, string? specimenDisplayName, CancellationToken ct)
    {
        // Недоступный ИИ (IsTransientFailure) — не отказ гейта: раньше обогащение в этом случае молча
        // терялось (а при смене источника пользователю писали «сочетание выглядит недостоверным»).
        // Теперь пропускаем — сам процессор обогащения повторит те же гейты и, если ИИ всё ещё
        // недоступен, оставит задачу в очереди на досып (LmStudioRecoverySweepJob).
        var legitimacy = await legitimacyGuard.CheckAsync(analyteName, ct);
        if (legitimacy.IsTransientFailure) return null;
        if (!legitimacy.IsLegitimate) return legitimacy.Reason;

        var plausibility = await plausibilityGuard.CheckAsync(analyteName, specimenDisplayName, ct);
        if (plausibility.IsTransientFailure) return null;
        if (!plausibility.IsPlausible) return plausibility.Reason;

        return null;
    }
    public async Task<(ExtractionQueryResult Result, ExtractionStatusResponse? Item)> GetStatusAsync(
        Guid recordId, Guid userId, CancellationToken ct = default)
    {
        var access = await CheckAccessAsync(recordId, userId, ct);
        if (access != ExtractionQueryResult.Success) return (access, null);

        var job = await db.MedicalDocumentExtractionJobs.AsNoTracking()
            .Where(j => j.MedicalRecordId == recordId)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (job is null) return (ExtractionQueryResult.NotFound, null);

        // Позиция в ОБЩЕЙ очереди к единственному локальному LLM — пока задача реально ждёт своей
        // очереди (Pending ИЛИ Running: "extraction"/"enrichment" — разные Hangfire-серверы,
        // задача может дойти до Running, пока Hangfire её взял, но модель всё ещё занята другой
        // задачей другого конвейера, см. class doc LlmQueuePositionService/LmStudioConcurrencyGate).
        // Раньше считалось только по своей таблице задач — систематически недооценивало реальное
        // ожидание при большом потоке задач обогащения справочника (баг, найденный на живом
        // отчёте: кнопка «Распознать» блокирована верно, а бейдж стадии создавал впечатление
        // активной работы, хотя задача просто стояла в общей очереди у семафора).
        var waitingForAi = job.WaitingForAi && job.Status == EnrichmentJobStatus.Pending;
        var queueAhead = !waitingForAi && job.Status is EnrichmentJobStatus.Pending or EnrichmentJobStatus.Running
            ? await queuePositionService.GetQueueAheadAsync(job.CreatedAt, ct)
            : 0;

        return (ExtractionQueryResult.Success, new ExtractionStatusResponse(
            job.Status, job.Stage, job.IndicatorCount, job.Error, job.TotalFiles, job.ProcessedFiles,
            job.CreatedAt, job.CompletedAt, queueAhead, job.CurrentThought, waitingForAi));
    }

    public async Task<(ExtractionQueryResult Result, List<IndicatorDto> Items)> GetIndicatorsAsync(
        Guid recordId, Guid userId, CancellationToken ct = default)
    {
        var access = await CheckAccessAsync(recordId, userId, ct, writeAudit: true);
        if (access != ExtractionQueryResult.Success) return (access, []);

        var items = await db.LabIndicators.AsNoTracking()
            .Where(i => i.MedicalRecordId == recordId)
            .OrderBy(i => i.Position)
            .ToListAsync(ct);

        var specimenNames = await ResolveSpecimenNamesAsync(items.Select(i => i.SpecimenKbId), ct);
        var pendingKeys = await GetPendingEnrichmentKeysAsync(items, ct);
        // Один поход в БД на всю страницу (см. class doc LlmQueuePositionService) — не на каждый
        // промахнувшийся показатель отдельно; лишний запрос вообще не делаем, если промахов нет.
        var activeTimestamps = pendingKeys.Count > 0
            ? await queuePositionService.GetActiveJobTimestampsAsync(ct)
            : [];

        return (ExtractionQueryResult.Success, items
            .Select(i =>
            {
                var (pending, liveText, createdAt, waitingForAi) = FindPendingEnrichment(i, pendingKeys);
                var queueAhead = pending && !waitingForAi ? LlmQueuePositionService.CountAhead(activeTimestamps, createdAt) : 0;
                return ToDto(i, specimenNames.GetValueOrDefault(i.SpecimenKbId), pending, liveText, queueAhead, waitingForAi);
            })
            .ToList());
    }

    /// <summary>§5 плана «живой конвейер» — один доп. запрос на всю СТРАНИЦУ показателей (не на
    /// каждый), т.к. GetIndicatorsAsync и так вызывается на каждое открытие записи. CurrentThought
    /// — живой обрывок "мысли" модели (план "живой поток мыслей") — non-null максимум на одной
    /// строке из всех активных задач всей системы одновременно, см. class doc ActiveJobItem.
    /// CreatedAt — для позиции в ОБЩЕЙ очереди к LLM (см. LlmQueuePositionService), не только
    /// среди показателей этой записи.</summary>
    private async Task<List<(string NormalizedName, Guid SpecimenKbId, string? CurrentThought, DateTime CreatedAt, bool WaitingForAi)>> GetPendingEnrichmentKeysAsync(
        List<DomainLabIndicator> items, CancellationToken ct)
    {
        var specimenIds = items.Select(i => i.SpecimenKbId).Distinct().ToList();
        if (specimenIds.Count == 0) return [];

        // Транзитно упавшие (ИИ был недоступен) тоже считаются «в процессе» — их подхватит
        // LmStudioRecoverySweepJob; без этого чип «уточняем норму» молча исчезал бы, и показатель
        // выглядел «справочник не нашёл» навсегда. Окно — те же 7 суток, что у самого досыпа.
        var waitingSince = DateTime.UtcNow.AddDays(-7);
        var rows = await db.LabAnalyteEnrichmentJobs.AsNoTracking()
            .Where(j => specimenIds.Contains(j.SpecimenKbId)
                && (j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running
                    || (j.Status == EnrichmentJobStatus.Failed && j.IsTransientFailure && j.CreatedAt > waitingSince)))
            .Select(j => new { j.NormalizedName, j.SpecimenKbId, j.CurrentThought, j.CreatedAt, Waiting = j.Status == EnrichmentJobStatus.Failed })
            .ToListAsync(ct);

        return rows.Select(r => (r.NormalizedName, r.SpecimenKbId, r.CurrentThought, r.CreatedAt, r.Waiting)).ToList();
    }

    /// <summary>Матчинг НЕ точным равенством — LabIndicator.AnalyteKey иногда несёт суффикс
    /// разведения коллизий («… — файл 2», см. AnalyteKeyDisambiguator), а джоба всегда стоит по
    /// БАЗОВОМУ ключу без суффикса (MedicalDocumentExtractionProcessor, комментарий "ПО БАЗОВОМУ
    /// ключу (lookupKey), не по разведённому analyteKey") — поэтому StartsWith, не ==. Ложных
    /// совпадений на практике не бывает: коллизия имени ПОСЛЕ нормализации в пределах одного
    /// источника — редкий случай, который и разводит AnalyteKeyDisambiguator.</summary>
    private static (bool Pending, string? LiveText, DateTime CreatedAt, bool WaitingForAi) FindPendingEnrichment(
        DomainLabIndicator indicator, List<(string NormalizedName, Guid SpecimenKbId, string? CurrentThought, DateTime CreatedAt, bool WaitingForAi)> pendingKeys)
    {
        var match = pendingKeys.FirstOrDefault(k => k.SpecimenKbId == indicator.SpecimenKbId
            && indicator.AnalyteKey.StartsWith(k.NormalizedName, StringComparison.Ordinal));
        return match.NormalizedName is null ? (false, null, default, false) : (true, match.CurrentThought, match.CreatedAt, match.WaitingForAi);
    }

    /// <summary>Заключение врача (Kind=DoctorVisit) — MedicalRecord.ExtractedDataJson, зеркало
    /// GetSummaryAsync для показателей анализа (Kind=Analysis использует SummaryJson, не это поле).
    /// Ссылки на справочник медикаментов (KbMedicationId) резолвятся ЖИВЫМ поиском на каждое
    /// чтение (см. PrescribedMedicationDto) — так подхватывается результат обогащения, даже если
    /// оно завершилось уже после первого просмотра заключения, без отдельного бэкофилла.</summary>
    public async Task<(ExtractionQueryResult Result, VisitConclusionResponse? Item)> GetConclusionAsync(
        Guid recordId, Guid userId, CancellationToken ct = default)
    {
        var access = await CheckAccessAsync(recordId, userId, ct, writeAudit: true);
        if (access != ExtractionQueryResult.Success) return (access, null);

        var extractedDataJson = await db.MedicalRecords.AsNoTracking()
            .Where(r => r.Id == recordId).Select(r => r.ExtractedDataJson).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(extractedDataJson)) return (ExtractionQueryResult.NotFound, null);

        var conclusion = JsonSerializer.Deserialize<VisitConclusion>(extractedDataJson);
        if (conclusion is null) return (ExtractionQueryResult.NotFound, null);

        var prescribed = conclusion.PrescribedMedications ?? [];

        // Батч точного совпадения на ВСЕ названия заключения одним запросом (аудит, находка
        // High #1) — покрывает частый случай (препарат уже в справочнике под тем же именем) без
        // кэша, поэтому "живой поиск на каждое чтение" из докстринга выше не нарушается: если
        // обогащение завершилось между двумя просмотрами, второй просмотр по-прежнему видит его
        // сразу. Для промахов — прежний поштучный каскад алиас/нечёткое совпадение ниже.
        var namesToResolve = prescribed
            .Select(m => MedicationNameNormalizer.Normalize(m.Name))
            .Where(n => n.Length > 0)
            .ToList();
        var exactHits = await medicationKbLookup.LookupExactManyAsync(namesToResolve, ct);

        var medications = new List<PrescribedMedicationDto>();
        foreach (var med in prescribed)
        {
            var normalizedName = MedicationNameNormalizer.Normalize(med.Name);
            Guid? kbMedicationId = null;
            if (normalizedName.Length > 0)
            {
                if (exactHits.TryGetValue(normalizedName, out var exactHit))
                {
                    kbMedicationId = exactHit.KbId;
                }
                else
                {
                    var lookup = await medicationKbLookup.LookupAsync(normalizedName, ct);
                    if (lookup.Kind == Kb.KbLookupKind.Hit) kbMedicationId = lookup.KbId;
                }
            }
            medications.Add(new PrescribedMedicationDto(med.Name, med.DosageInstructions, kbMedicationId));
        }

        return (ExtractionQueryResult.Success, new VisitConclusionResponse(
            conclusion.Diagnosis, conclusion.Recommendations, conclusion.Anamnesis, conclusion.ProceduresPerformed, medications));
    }

    public async Task<(ExtractionQueryResult Result, RecordSummaryResponse? Item)> GetSummaryAsync(
        Guid recordId, Guid userId, CancellationToken ct = default)
    {
        var access = await CheckAccessAsync(recordId, userId, ct, writeAudit: true);
        if (access != ExtractionQueryResult.Success) return (access, null);

        var state = await db.MedicalRecords.AsNoTracking()
            .Where(r => r.Id == recordId).Select(r => new { r.SummaryJson, r.SummaryDirtyAt }).FirstOrDefaultAsync(ct);
        var pending = state?.SummaryDirtyAt is not null;

        var summary = string.IsNullOrEmpty(state?.SummaryJson) ? null : JsonSerializer.Deserialize<LabSummary>(state.SummaryJson);
        if (summary is null)
        {
            // Пересчёт после правки уже запланирован, но старое резюме нет (или сброшено) —
            // отвечаем «в работе», а не 404: фронт по Pending показывает «Обновляем резюме…» и
            // опрашивает эндпоинт, пока текст не появится.
            return pending
                ? (ExtractionQueryResult.Success, new RecordSummaryResponse(null, [], [], string.Empty, Pending: true))
                : (ExtractionQueryResult.NotFound, null);
        }

        return (ExtractionQueryResult.Success, new RecordSummaryResponse(
            summary.PlainSummary, summary.Deviations, summary.QuestionsForDoctor, summary.Disclaimer, pending));
    }

    /// <summary>Помечает резюме записи устаревшим и планирует его фоновый пересчёт (см. class doc
    /// RecordSummaryRegenerationJob — дебаунс по токену). Зовётся после каждой ручной правки,
    /// меняющей входные данные резюме: показатель добавлен/изменён/удалён, сменён источник записи.
    /// Сбой постановки в Hangfire не должен ронять саму правку — тогда пометка снимается, и резюме
    /// остаётся как есть (фронт при следующем открытии всё равно может запросить пересчёт).</summary>
    private Task MarkSummaryDirtyAsync(Guid recordId, CancellationToken ct) =>
        RecordSummaryRegenerationJob.RescheduleAsync(db, backgroundJobs, recordId, RecordSummaryRegenerationJob.Delay, ct);

    /// <summary>Пересчитывает "Резюме"/"Вопросы врачу" по ТЕКУЩИМ показателям записи — независимо
    /// от исходной автоматической суммаризации при распознавании. Нужен, когда OCR неверно
    /// прочитал значение/референс с бланка (см. IndicatorFlagCalculator), из-за чего исходное
    /// резюме построено на неверных цифрах: пользователь правит показатель вручную
    /// (UpdateIndicatorAsync), а резюме само не пересчитывается — этот метод даёт явную кнопку
    /// вместо того, чтобы заставлять пересканировать документ заново (который вернул бы ту же
    /// ошибку OCR). Теперь только ставит фоновый пересчёт (202 + pending), сам LLM не зовёт.</summary>
    public async Task<(ExtractionQueryResult Result, RecordSummaryResponse? Item)> RegenerateSummaryAsync(
        Guid recordId, Guid userId, CancellationToken ct = default)
    {
        var record = await db.MedicalRecords.FirstOrDefaultAsync(r => r.Id == recordId, ct);
        if (record is null) return (ExtractionQueryResult.NotFound, null);
        if (record.OwnerUserId != userId) return (ExtractionQueryResult.Forbidden, null);

        if (!await db.LabIndicators.AnyAsync(i => i.MedicalRecordId == recordId, ct))
            return (ExtractionQueryResult.NotFound, null);

        // Пересчёт идёт в фоне (без задержки-дебаунса): если ИИ недоступен, задача не теряется —
        // пометка SummaryDirtyAt остаётся, LmStudioRecoverySweepJob запустит её, когда сервер вернётся.
        // Раньше здесь был синхронный вызов LLM из HTTP-запроса с ответом 502 при недоступном сервере.
        await RecordSummaryRegenerationJob.RescheduleAsync(db, backgroundJobs, recordId, TimeSpan.Zero, ct);

        return (ExtractionQueryResult.Success, new RecordSummaryResponse(
            record.SummaryJson is null ? null : JsonSerializer.Deserialize<LabSummary>(record.SummaryJson)?.PlainSummary,
            [], [], string.Empty, Pending: true));
    }

    /// <summary>Последнее значение по каждому (показатель, источник, ПАЦИЕНТ) среди СВОИХ записей
    /// пользователя (владелец) — расшаренные чужие записи сюда не входят, "мои показатели" в
    /// буквальном смысле "загруженные мной". SpecimenKbId — часть ключа группировки (пересборка
    /// enrich-пайплайна): лейкоциты крови и мочи не должны схлопнуться в одну строку.
    /// FamilyDependentId/TargetUserId — ТАК ЖЕ обязательная часть ключа: без неё показатели РАЗНЫХ
    /// членов семьи (например, несколько человек сдавали АЛТ) схлопывались в одну строку/график —
    /// реальный баг, см. class doc LabIndicator.FamilyDependentId.</summary>
    public async Task<List<MyIndicatorSummary>> GetMyIndicatorsAsync(Guid userId, CancellationToken ct = default)
    {
        var all = await db.LabIndicators.AsNoTracking()
            .Where(i => i.OwnerUserId == userId)
            .ToListAsync(ct);

        var latest = all
            .GroupBy(i => (i.FamilyDependentId, i.TargetUserId, i.AnalyteKey, i.SpecimenKbId))
            .Select(g => g.OrderByDescending(i => i.RecordDate).First())
            .ToList();

        var specimenNames = await ResolveSpecimenNamesAsync(latest.Select(i => i.SpecimenKbId), ct);
        var patientNames = await PatientIdentityResolver.ResolvePatientNamesAsync(
            db, latest.Select(i => (i.FamilyDependentId, i.TargetUserId, i.OwnerUserId)), ct);

        return latest
            .Select(i => new MyIndicatorSummary(
                i.AnalyteKey, i.DisplayName, i.SpecimenKbId, specimenNames.GetValueOrDefault(i.SpecimenKbId),
                i.ValueRaw, i.Unit, i.Flag, i.RecordDate,
                i.FamilyDependentId, i.TargetUserId,
                patientNames.GetValueOrDefault((i.FamilyDependentId, i.TargetUserId), "Я")))
            .OrderBy(s => s.DisplayName)
            .ToList();
    }

    public async Task<List<IndicatorHistoryPoint>> GetHistoryAsync(
        Guid userId, string analyteKey, Guid specimenKbId, Guid? familyDependentId, Guid? targetUserId, CancellationToken ct = default)
    {
        var items = await db.LabIndicators.AsNoTracking()
            .Where(i => i.OwnerUserId == userId && i.AnalyteKey == analyteKey && i.SpecimenKbId == specimenKbId
                && i.FamilyDependentId == familyDependentId && i.TargetUserId == targetUserId)
            .OrderBy(i => i.RecordDate)
            .ToListAsync(ct);

        return items.Select(i => new IndicatorHistoryPoint(i.RecordDate, i.ValueRaw, i.ValueNumericText, i.Flag, i.MedicalRecordId)).ToList();
    }

    /// <summary>Батч-резолв DisplayName источников на набор SpecimenKbId — один запрос вместо N+1,
    /// тот же приём, что exactHits в GetConclusionAsync.</summary>
    private async Task<Dictionary<Guid, string>> ResolveSpecimenNamesAsync(IEnumerable<Guid> specimenKbIds, CancellationToken ct)
    {
        var distinct = specimenKbIds.Distinct().ToList();
        if (distinct.Count == 0) return [];

        return await db.GlobalSpecimensKb.AsNoTracking()
            .Where(s => distinct.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.DisplayName, ct);
    }

    /// <summary>Персонализированная статья справочника по показателю (редизайн v2, панель справки) —
    /// показатель + возраст/пол пациента ЭТОЙ записи (не "сегодня") + подсвеченный диапазон норм +
    /// доступность "Динамики". Доступ — тот же CheckAccessAsync, что и у остальных чтений
    /// показателей; аудит не пишем — просмотр уже зафиксирован при GetIndicatorsAsync, статья —
    /// производный от него клик, не отдельный факт доступа к чужим данным.</summary>
    public async Task<(ExtractionQueryResult Result, IndicatorArticleResponse? Item)> GetArticleAsync(
        Guid indicatorId, Guid userId, CancellationToken ct = default)
    {
        var indicator = await db.LabIndicators.AsNoTracking().FirstOrDefaultAsync(i => i.Id == indicatorId, ct);
        if (indicator is null) return (ExtractionQueryResult.NotFound, null);

        var access = await CheckAccessAsync(indicator.MedicalRecordId, userId, ct);
        if (access != ExtractionQueryResult.Success) return (access, null);

        var record = await db.MedicalRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == indicator.MedicalRecordId, ct);
        if (record is null) return (ExtractionQueryResult.NotFound, null); // защитно — не должно случиться, раз показатель на неё ссылается

        var (ageYears, sex) = await PatientIdentityResolver.ResolveAsync(db, record, ct);

        Kb.KbAnalyteCard? article = null;
        int? matchedIndex = null;
        if (indicator.KbAnalyteId is { } kbId)
        {
            article = await analyteCatalog.GetByIdAsync(kbId, ct);
            if (article is not null && article.RefRanges.Count > 0)
            {
                // KbRefRangeDto/KbReferenceRange — одинаковые по форме, но разные типы (DTO ответа
                // vs внутренний тип каскада расчёта статуса) — конвертация, не общий тип специально,
                // чтобы не тащить зависимость каскада в контракт ответа API. NormKind/Population
                // ОБЯЗАТЕЛЬНО прокидываются дальше (не default) — иначе PickBestRangeIndex ниже не
                // сможет отфильтровать Pregnancy/CyclePhase/Qualitative строки (пересборка enrich-пайплайна).
                var ranges = article.RefRanges
                    .Select(r => new KbReferenceRange(
                        r.AgeFrom, r.AgeTo, r.Sex, r.Low, r.High, r.Unit,
                        r.NormKind, r.Population, r.PopulationDetail, r.SourceDomain))
                    .ToList();
                matchedIndex = IndicatorFlagCalculator.PickBestRangeIndex(ranges, ageYears, sex);
            }
        }

        var historyCount = (await QueryVisibleHistoryAsync(indicator, userId, ct)).Count;
        var specimenDisplayName = await db.GlobalSpecimensKb.AsNoTracking()
            .Where(s => s.Id == indicator.SpecimenKbId).Select(s => s.DisplayName).FirstOrDefaultAsync(ct);

        return (ExtractionQueryResult.Success, new IndicatorArticleResponse(
            ToDto(indicator, specimenDisplayName), new PatientContextDto(ageYears, sex), matchedIndex, article, historyCount >= 2));
    }

    /// <summary>Тренд показателя для КОНКРЕТНОЙ записи (в отличие от GetHistoryAsync выше, который
    /// строго "свои" — этот работает и для расшаренной чужой записи). Тройной фильтр обязателен:
    /// владелец записи + ТА ЖЕ идентичность пациента (FamilyDependentId/TargetUserId — иначе точки
    /// тренда РАЗНЫХ членов семьи с одинаковым показателем смешались бы, см. class doc
    /// LabIndicator.FamilyDependentId) И видимость КАЖДОЙ точки лично зрителю — без последнего
    /// условия тренд по одной расшаренной записи обошёл бы точечное скрытие MedicalRecordHidden
    /// (L2), см. риск Р5 плана редизайна.</summary>
    public async Task<(ExtractionQueryResult Result, List<IndicatorHistoryPoint> Items)> GetRecordIndicatorHistoryAsync(
        Guid recordId, Guid indicatorId, Guid userId, CancellationToken ct = default)
    {
        var access = await CheckAccessAsync(recordId, userId, ct);
        if (access != ExtractionQueryResult.Success) return (access, []);

        var indicator = await db.LabIndicators.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == indicatorId && i.MedicalRecordId == recordId, ct);
        if (indicator is null) return (ExtractionQueryResult.NotFound, []);

        return (ExtractionQueryResult.Success, await QueryVisibleHistoryAsync(indicator, userId, ct));
    }

    private async Task<List<IndicatorHistoryPoint>> QueryVisibleHistoryAsync(DomainLabIndicator indicator, Guid userId, CancellationToken ct)
    {
        var visibleIds = await medicalRecords.GetVisibleRecordIdsAsync(userId, MedicalRecordKind.Analysis, ct);
        var items = await db.LabIndicators.AsNoTracking()
            .Where(i => i.OwnerUserId == indicator.OwnerUserId && i.AnalyteKey == indicator.AnalyteKey
                && i.SpecimenKbId == indicator.SpecimenKbId
                && i.FamilyDependentId == indicator.FamilyDependentId && i.TargetUserId == indicator.TargetUserId
                && visibleIds.Contains(i.MedicalRecordId))
            .OrderBy(i => i.RecordDate)
            .ToListAsync(ct);

        return items.Select(i => new IndicatorHistoryPoint(i.RecordDate, i.ValueRaw, i.ValueNumericText, i.Flag, i.MedicalRecordId)).ToList();
    }

    /// <summary>Правка показателя вручную (ошибка OCR) — только владелец мед-записи. Ref-поля,
    /// присланные в запросе, становятся новым "референсом с бланка" (RefSource.Blank) — ручная
    /// правка семантически заменяет то, что распознала модель, тем же приоритетом, что и печатный
    /// бланк; KB/расчётный каскад заново не гоняется для САМОГО этого показателя (пользователь
    /// правит конкретные цифры, а не просит переопределить справочником). Flag пересчитывается
    /// тем же компаратором, что и при автораспознавании — не дублируем пороговую логику. Источник
    /// (SpecimenKbId) здесь не меняется — это атрибут ВСЕЙ записи (заметка 1), правится отдельно
    /// через SetRecordSpecimenAsync.
    ///
    /// В СПРАВОЧНИК же обогащение теперь ставится в очередь при промахе, тем же путём, что и
    /// CreateIndicatorAsync (см. её class doc) — исправленное после правки название могло увести
    /// показатель на пару (AnalyteKey, SpecimenKbId), которой ещё нет в KB.</summary>
    public async Task<UpdateIndicatorResult> UpdateIndicatorAsync(
        Guid indicatorId, Guid userId, UpdateIndicatorRequest request, CancellationToken ct = default)
    {
        var indicator = await db.LabIndicators.FirstOrDefaultAsync(i => i.Id == indicatorId, ct);
        if (indicator is null) return UpdateIndicatorResult.NotFound;
        if (indicator.OwnerUserId != userId) return UpdateIndicatorResult.Forbidden;

        var displayName = request.DisplayName.Trim();
        if (displayName.Length == 0) return UpdateIndicatorResult.NotFound;

        var analyteKey = LabAnalyteNormalizer.NormalizeAnalyteKey(displayName);
        if (analyteKey.Length == 0) analyteKey = indicator.AnalyteKey;

        // Уникальный индекс (MedicalRecordId, AnalyteKey, SpecimenKbId) — правка могла увести
        // показатель на имя, уже занятое другой строкой этой же записи (источник у обеих строк
        // один и тот же — атрибут записи, не показателя).
        var conflict = await db.LabIndicators.AnyAsync(i =>
            i.Id != indicatorId && i.MedicalRecordId == indicator.MedicalRecordId &&
            i.AnalyteKey == analyteKey && i.SpecimenKbId == indicator.SpecimenKbId, ct);
        if (conflict) return UpdateIndicatorResult.Conflict;

        var refLow = ParseNumeric(request.RefLowText);
        var refHigh = ParseNumeric(request.RefHighText);
        var refText = string.IsNullOrWhiteSpace(request.RefText) ? null : request.RefText.Trim();

        var dto = new ExtractedLabIndicator(displayName, request.ValueRaw, request.Unit, refLow, refHigh, refText);
        var (flag, refSource, effLow, effHigh) = IndicatorFlagCalculator.Calculate(dto, kbFallback: null, ageYears: null, sex: null);

        indicator.DisplayName = displayName;
        // Ручная правка полностью заменяет исходную формулировку с бланка — RawDisplayName больше
        // не актуален, подсказка "в бланке: …" исчезает из UI (тот же приём, что сброс других
        // распознанных полей ручной правкой ниже).
        indicator.RawDisplayName = null;
        indicator.AnalyteKey = analyteKey;
        indicator.ValueRaw = request.ValueRaw;
        indicator.ValueNumericText = ParseNumeric(request.ValueRaw)?.ToString(CultureInfo.InvariantCulture);
        indicator.Unit = request.Unit;
        indicator.RefLowText = effLow?.ToString(CultureInfo.InvariantCulture);
        indicator.RefHighText = effHigh?.ToString(CultureInfo.InvariantCulture);
        indicator.RefText = refText;
        indicator.Flag = flag;
        indicator.RefSource = refSource;

        await db.SaveChangesAsync(ct);
        await MarkSummaryDirtyAsync(indicator.MedicalRecordId, ct);

        // Промах справочника по (показатель, источник) после правки — ставим обогащение в
        // очередь, тем же единственным входом, что и CreateIndicatorAsync (см. её class doc), но
        // только после синхронной проверки гейтами (заметка 3) — раньше отказ был виден только
        // задним числом в Job.Status=Failed внутри фонового процессора.
        var lookup = await analyteKbLookup.LookupAsync(analyteKey, indicator.SpecimenKbId, ct);
        // Нерезолвленный источник и так навсегда отклонён внутри RequestAsync (жёсткое правило) —
        // не тратим гейт-вызов LLM на заведомо обречённую постановку в очередь.
        if (lookup.Kind != Kb.KbLookupKind.Hit && indicator.SpecimenKbId != Domain.Entities.SpecimenContextIds.Unresolved)
        {
            var specimenDisplayName = await db.GlobalSpecimensKb.AsNoTracking()
                .Where(s => s.Id == indicator.SpecimenKbId).Select(s => s.DisplayName).FirstOrDefaultAsync(ct);
            if (await CheckManualEntryGatesAsync(displayName, specimenDisplayName, ct) is null)
                await enrichmentRequest.RequestAsync(
                    analyteKey, indicator.SpecimenKbId, displayName, indicator.Id, userId,
                    origin: EnrichmentRequestOrigin.ManualEntry, ct: ct);
        }

        return UpdateIndicatorResult.Success;
    }

    /// <summary>Ручная смена/уточнение источника ВСЕЙ записи (заметка 1) — единственный путь
    /// изменить MedicalRecord.SpecimenKbId после распознавания (тот же барьер владельца, что и
    /// остальные мутации записи). Каскадится на все LabIndicators записи разом — источник у них
    /// денормализован (см. class doc LabIndicator.SpecimenKbId), правка одного показателя больше
    /// невозможна, только всей записи целиком. SpecimenHint сбрасывается — раз источник уточнён,
    /// подсказка "уточните источник" в UI больше не нужна.
    ///
    /// Warning в ответе (заметка 3) — не null, если гейты (см. CheckManualEntryGatesAsync)
    /// отклонили обогащение хотя бы одного промахнувшегося показателя под НОВЫМ источником: смена
    /// источника сама по себе всегда сохраняется (Success), отклонение гейта — только про то, что
    /// в общий справочник по этой паре ничего не уйдёт.</summary>
    public async Task<(SetRecordSpecimenResult Result, string? Warning)> SetRecordSpecimenAsync(
        Guid recordId, Guid userId, Guid specimenKbId, CancellationToken ct = default)
    {
        var record = await db.MedicalRecords.FirstOrDefaultAsync(r => r.Id == recordId, ct);
        if (record is null) return (SetRecordSpecimenResult.NotFound, null);
        if (record.OwnerUserId != userId) return (SetRecordSpecimenResult.Forbidden, null);

        var specimenDisplayName = await db.GlobalSpecimensKb.AsNoTracking()
            .Where(s => s.Id == specimenKbId).Select(s => (string?)s.DisplayName).FirstOrDefaultAsync(ct);
        if (specimenDisplayName is null) return (SetRecordSpecimenResult.NotFound, null);

        var indicators = await db.LabIndicators.AsNoTracking()
            .Where(i => i.MedicalRecordId == recordId)
            .Select(i => new { i.Id, i.AnalyteKey, i.DisplayName })
            .ToListAsync(ct);

        record.SpecimenKbId = specimenKbId;
        record.SpecimenHint = null;
        await db.SaveChangesAsync(ct);

        if (indicators.Count > 0)
        {
            try
            {
                await db.LabIndicators.Where(i => i.MedicalRecordId == recordId)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.SpecimenKbId, specimenKbId), ct);
            }
            catch (DbUpdateException)
            {
                // Унаследованный случай (запись, собранная ДО этой правки старым посекционным
                // резолвером) — тот же показатель уже дважды заведён под разными источниками;
                // после каскада оба легли бы на одну пару (AnalyteKey, specimenKbId), нарушив
                // уникальный индекс. Откатываем и правку записи — источник не сменён нигде.
                await db.Entry(record).ReloadAsync(ct);
                return (SetRecordSpecimenResult.Conflict, null);
            }
        }

        if (indicators.Count > 0) await MarkSummaryDirtyAsync(recordId, ct);

        // Промах справочника по любому из показателей записи на новую пару — ставим обогащение в
        // очередь, тем же единственным входом, что и Update/CreateIndicatorAsync, но только после
        // синхронной проверки гейтами (заметка 3) — иначе отказ был бы виден только задним числом
        // в Job.Status=Failed внутри фонового процессора.
        var rejectedNames = new List<string>();
        var isResolved = specimenKbId != Domain.Entities.SpecimenContextIds.Unresolved;
        foreach (var indicator in indicators)
        {
            var lookup = await analyteKbLookup.LookupAsync(indicator.AnalyteKey, specimenKbId, ct);
            if (lookup.Kind == Kb.KbLookupKind.Hit || !isResolved) continue;

            if (await CheckManualEntryGatesAsync(indicator.DisplayName, specimenDisplayName, ct) is null)
                await enrichmentRequest.RequestAsync(
                    indicator.AnalyteKey, specimenKbId, indicator.DisplayName, indicator.Id, userId,
                    origin: EnrichmentRequestOrigin.ManualEntry, ct: ct);
            else
                rejectedNames.Add(indicator.DisplayName);
        }

        var warning = rejectedNames.Count == 0 ? null
            : $"Источник сохранён, но для показателей ({string.Join(", ", rejectedNames)}) он не будет добавлен в общий справочник — сочетание выглядит недостоверным.";
        return (SetRecordSpecimenResult.Success, warning);
    }

    /// <summary>Ручной биоматериал, пока ИИ недоступен: проверка названия моделью (POST /api/specimens)
    /// невозможна, поэтому сохраняем введённое как «ожидает проверки» — LmStudioRecoverySweepJob
    /// проверит и применит его к записи, когда сервер вернётся (или превратит в подсказку «уточните
    /// источник», если модель отклонит). Только владелец записи — как и у остальных мутаций.</summary>
    public async Task<SetPendingSpecimenResult> SetPendingSpecimenAsync(
        Guid recordId, Guid userId, string? name, CancellationToken ct = default)
    {
        var record = await db.MedicalRecords.FirstOrDefaultAsync(r => r.Id == recordId, ct);
        if (record is null) return SetPendingSpecimenResult.NotFound;
        if (record.OwnerUserId != userId) return SetPendingSpecimenResult.Forbidden;

        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is < 2 or > 60) return SetPendingSpecimenResult.InvalidInput;

        record.PendingSpecimenText = trimmed;
        await db.SaveChangesAsync(ct);
        return SetPendingSpecimenResult.Success;
    }

    /// <summary>Ручное добавление показателя (UX-редизайн) — тот же путь расчёта флага, что и
    /// правка: RefSource.Blank, KB-каскад заново не гоняется для САМОГО этого показателя
    /// (пользователь вводит конкретные цифры руками, не просит распознать заново). Position — в
    /// конец текущего списка записи.
    ///
    /// В СПРАВОЧНИК же обогащение теперь ставится в очередь при промахе — ровно тем же путём и с
    /// теми же гарантиями, что у распознавания документа (см. MedicalDocumentExtractionProcessor):
    /// единственная точка входа LabAnalyteEnrichmentRequestService сама гейтует нерезолвленный
    /// источник, а первый шаг самого фонового конвейера (LegitimacyGuardService,
    /// PipelineCatalog.LegitimacyCheckStep) проверяет введённое название на легитимность и prompt
    /// injection ДО web-поиска/LLM-суммаризации — отклонённое название просто проваливает задачу
    /// обогащения (Job.Status=Failed), не блокируя сохранение самого показателя: это личные данные
    /// пользователя, им они распоряжается всегда, обогащение — только побочная попытка связать их
    /// с общим справочником.</summary>
    public async Task<(CreateIndicatorResult Result, IndicatorDto? Item)> CreateIndicatorAsync(
        Guid recordId, Guid userId, CreateIndicatorRequest request, CancellationToken ct = default)
    {
        var record = await db.MedicalRecords.AsNoTracking()
            .Where(r => r.Id == recordId)
            .Select(r => new { r.Id, r.OwnerUserId, r.FamilyDependentId, r.TargetUserId, r.RecordDate, r.SpecimenKbId })
            .FirstOrDefaultAsync(ct);
        if (record is null) return (CreateIndicatorResult.NotFound, null);
        if (record.OwnerUserId != userId) return (CreateIndicatorResult.Forbidden, null);

        var displayName = request.DisplayName.Trim();
        if (displayName.Length == 0) return (CreateIndicatorResult.NotFound, null);

        var analyteKey = LabAnalyteNormalizer.NormalizeAnalyteKey(displayName);
        if (analyteKey.Length == 0) return (CreateIndicatorResult.NotFound, null);

        // Источник — атрибут ВСЕЙ записи, не этого запроса (заметка 1): наследуется от record,
        // меняется отдельно через SetRecordSpecimenAsync.
        var conflict = await db.LabIndicators.AnyAsync(i =>
            i.MedicalRecordId == recordId && i.AnalyteKey == analyteKey && i.SpecimenKbId == record.SpecimenKbId, ct);
        if (conflict) return (CreateIndicatorResult.Conflict, null);

        var refLow = ParseNumeric(request.RefLowText);
        var refHigh = ParseNumeric(request.RefHighText);
        var refText = string.IsNullOrWhiteSpace(request.RefText) ? null : request.RefText.Trim();
        var dto = new ExtractedLabIndicator(displayName, request.ValueRaw, request.Unit, refLow, refHigh, refText);
        var (flag, refSource, effLow, effHigh) = IndicatorFlagCalculator.Calculate(dto, kbFallback: null, ageYears: null, sex: null);

        var maxPosition = await db.LabIndicators
            .Where(i => i.MedicalRecordId == recordId)
            .Select(i => (int?)i.Position)
            .MaxAsync(ct) ?? -1;

        var indicator = new DomainLabIndicator
        {
            Id = Guid.NewGuid(),
            MedicalRecordId = recordId,
            RecordDate = record.RecordDate,
            OwnerUserId = userId,
            FamilyDependentId = record.FamilyDependentId,
            TargetUserId = record.TargetUserId,
            AnalyteKey = analyteKey,
            DisplayName = displayName,
            Flag = flag,
            RefSource = refSource,
            SpecimenKbId = record.SpecimenKbId,
            Position = maxPosition + 1,
            ValueRaw = request.ValueRaw,
            ValueNumericText = ParseNumeric(request.ValueRaw)?.ToString(CultureInfo.InvariantCulture),
            Unit = request.Unit,
            RefLowText = effLow?.ToString(CultureInfo.InvariantCulture),
            RefHighText = effHigh?.ToString(CultureInfo.InvariantCulture),
            RefText = refText,
            CreatedAt = DateTime.UtcNow,
        };
        db.LabIndicators.Add(indicator);
        await db.SaveChangesAsync(ct);
        await MarkSummaryDirtyAsync(recordId, ct);

        var specimenDisplayName = await db.GlobalSpecimensKb.AsNoTracking()
            .Where(s => s.Id == record.SpecimenKbId).Select(s => s.DisplayName).FirstOrDefaultAsync(ct);

        // Промах справочника по (показатель, источник) — ставим обогащение в очередь, тем же
        // единственным входом, что и распознавание документа (см. class doc выше), но только
        // после синхронной проверки гейтами (заметка 3, см. CheckManualEntryGatesAsync). Гейт на
        // нерезолвленный источник и дедуп — внутри RequestAsync, не здесь.
        var lookup = await analyteKbLookup.LookupAsync(analyteKey, record.SpecimenKbId, ct);
        if (lookup.Kind != Kb.KbLookupKind.Hit && record.SpecimenKbId != Domain.Entities.SpecimenContextIds.Unresolved &&
            await CheckManualEntryGatesAsync(displayName, specimenDisplayName, ct) is null)
            await enrichmentRequest.RequestAsync(
                analyteKey, record.SpecimenKbId, displayName, indicator.Id, userId,
                origin: EnrichmentRequestOrigin.ManualEntry, ct: ct);

        return (CreateIndicatorResult.Success, ToDto(indicator, specimenDisplayName));
    }

    /// <summary>Удаление ошибочно распознанного/добавленного показателя — только владелец
    /// записи. Без него редактируемая таблица не покрывает основной сценарий правки: OCR иногда
    /// придумывает строку целиком, не только искажает значение в существующей.</summary>
    public async Task<DeleteIndicatorResult> DeleteIndicatorAsync(Guid indicatorId, Guid userId, CancellationToken ct = default)
    {
        var indicator = await db.LabIndicators.FirstOrDefaultAsync(i => i.Id == indicatorId, ct);
        if (indicator is null) return DeleteIndicatorResult.NotFound;
        if (indicator.OwnerUserId != userId) return DeleteIndicatorResult.Forbidden;

        var recordId = indicator.MedicalRecordId;
        db.LabIndicators.Remove(indicator);
        await db.SaveChangesAsync(ct);
        await MarkSummaryDirtyAsync(recordId, ct);
        return DeleteIndicatorResult.Success;
    }

    private async Task<ExtractionQueryResult> CheckAccessAsync(Guid recordId, Guid userId, CancellationToken ct, bool writeAudit = false)
    {
        var record = await db.MedicalRecords.AsNoTracking()
            .Where(r => r.Id == recordId).Select(r => new { r.Id, r.OwnerUserId }).FirstOrDefaultAsync(ct);
        if (record is null) return ExtractionQueryResult.NotFound;

        if (!await medicalRecords.IsVisibleToAsync(recordId, userId, ct)) return ExtractionQueryResult.Forbidden;

        if (writeAudit && record.OwnerUserId != userId)
            await audit.WriteAsync(userId, MedicalAccessAction.ViewList, ownerUserId: record.OwnerUserId, medicalRecordId: recordId, ct: ct);

        return ExtractionQueryResult.Success;
    }

    private static IndicatorDto ToDto(
        DomainLabIndicator i, string? specimenDisplayName, bool enrichmentPending = false, string? enrichmentLiveText = null,
        int enrichmentQueueAhead = 0, bool enrichmentWaitingForAi = false) => new(
        i.Id, i.AnalyteKey, i.DisplayName, i.Flag, i.RefSource, i.SpecimenKbId, specimenDisplayName, i.Position,
        i.ValueRaw, i.Unit, i.RefLowText, i.RefHighText, i.RefText, i.RecordDate, i.MedicalRecordId,
        i.ValueNumericText, i.KbAnalyteId, i.RawDisplayName, enrichmentPending, enrichmentLiveText, enrichmentQueueAhead,
        enrichmentWaitingForAi);

    private static double? ParseNumeric(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;
    }
}
