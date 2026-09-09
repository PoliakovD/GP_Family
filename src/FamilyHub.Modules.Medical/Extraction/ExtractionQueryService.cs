using System.Globalization;
using System.Text.Json;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Audit;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.MedicalRecords;
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
    Kb.KbAnalyteCatalogService analyteCatalog, IMedicalAuditWriter audit, LabSummarizer summarizer,
    LabAnalyteKbLookupService analyteKbLookup, LabAnalyteEnrichmentRequestService enrichmentRequest)
{
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

        return (ExtractionQueryResult.Success, new ExtractionStatusResponse(
            job.Status, job.Stage, job.IndicatorCount, job.Error, job.TotalFiles, job.ProcessedFiles, job.CreatedAt, job.CompletedAt));
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
        return (ExtractionQueryResult.Success, items.Select(i => ToDto(i, specimenNames.GetValueOrDefault(i.SpecimenKbId))).ToList());
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

        var summaryJson = await db.MedicalRecords.AsNoTracking()
            .Where(r => r.Id == recordId).Select(r => r.SummaryJson).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(summaryJson)) return (ExtractionQueryResult.NotFound, null);

        var summary = JsonSerializer.Deserialize<LabSummary>(summaryJson);
        if (summary is null) return (ExtractionQueryResult.NotFound, null);

        return (ExtractionQueryResult.Success, new RecordSummaryResponse(
            summary.PlainSummary, summary.Deviations, summary.QuestionsForDoctor, summary.Disclaimer));
    }

    /// <summary>Пересчитывает "Резюме"/"Вопросы врачу" по ТЕКУЩИМ показателям записи — независимо
    /// от исходной автоматической суммаризации при распознавании. Нужен, когда OCR неверно
    /// прочитал значение/референс с бланка (см. IndicatorFlagCalculator), из-за чего исходное
    /// резюме построено на неверных цифрах: пользователь правит показатель вручную
    /// (UpdateIndicatorAsync), а резюме само не пересчитывается — этот метод даёт явную кнопку
    /// вместо того, чтобы заставлять пересканировать документ заново (который вернул бы ту же
    /// ошибку OCR). Синхронный вызов LLM из HTTP-запроса — тот же приём, что MedicationOcrService.</summary>
    public async Task<(ExtractionQueryResult Result, RecordSummaryResponse? Item)> RegenerateSummaryAsync(
        Guid recordId, Guid userId, CancellationToken ct = default)
    {
        var record = await db.MedicalRecords.FirstOrDefaultAsync(r => r.Id == recordId, ct);
        if (record is null) return (ExtractionQueryResult.NotFound, null);
        if (record.OwnerUserId != userId) return (ExtractionQueryResult.Forbidden, null);

        var indicators = await db.LabIndicators.Where(i => i.MedicalRecordId == recordId).ToListAsync(ct);
        if (indicators.Count == 0) return (ExtractionQueryResult.NotFound, null);

        var summarized = await summarizer.SummarizeAsync(indicators, ct);
        if (!summarized.Success || summarized.Summary is null) return (ExtractionQueryResult.Failed, null);

        record.SummaryJson = JsonSerializer.Serialize(summarized.Summary);
        await db.SaveChangesAsync(ct);

        return (ExtractionQueryResult.Success, new RecordSummaryResponse(
            summarized.Summary.PlainSummary, summarized.Summary.Deviations, summarized.Summary.QuestionsForDoctor, summarized.Summary.Disclaimer));
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

        var analyteKey = LabAnalyteNormalizer.Normalize(displayName);
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

        // Промах справочника по (показатель, источник) после правки — ставим обогащение в
        // очередь, тем же единственным входом, что и CreateIndicatorAsync (см. её class doc).
        var lookup = await analyteKbLookup.LookupAsync(analyteKey, indicator.SpecimenKbId, ct);
        if (lookup.Kind != Kb.KbLookupKind.Hit)
            await enrichmentRequest.RequestAsync(
                analyteKey, indicator.SpecimenKbId, displayName, indicator.Id, userId,
                origin: EnrichmentRequestOrigin.ManualEntry, ct: ct);

        return UpdateIndicatorResult.Success;
    }

    /// <summary>Ручная смена/уточнение источника ВСЕЙ записи (заметка 1) — единственный путь
    /// изменить MedicalRecord.SpecimenKbId после распознавания (тот же барьер владельца, что и
    /// остальные мутации записи). Каскадится на все LabIndicators записи разом — источник у них
    /// денормализован (см. class doc LabIndicator.SpecimenKbId), правка одного показателя больше
    /// невозможна, только всей записи целиком. SpecimenHint сбрасывается — раз источник уточнён,
    /// подсказка "уточните источник" в UI больше не нужна.</summary>
    public async Task<SetRecordSpecimenResult> SetRecordSpecimenAsync(
        Guid recordId, Guid userId, Guid specimenKbId, CancellationToken ct = default)
    {
        var record = await db.MedicalRecords.FirstOrDefaultAsync(r => r.Id == recordId, ct);
        if (record is null) return SetRecordSpecimenResult.NotFound;
        if (record.OwnerUserId != userId) return SetRecordSpecimenResult.Forbidden;

        if (!await db.GlobalSpecimensKb.AnyAsync(s => s.Id == specimenKbId, ct))
            return SetRecordSpecimenResult.NotFound;

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
                return SetRecordSpecimenResult.Conflict;
            }
        }

        // Промах справочника по любому из показателей записи на новую пару — ставим обогащение в
        // очередь, тем же единственным входом, что и Update/CreateIndicatorAsync.
        foreach (var indicator in indicators)
        {
            var lookup = await analyteKbLookup.LookupAsync(indicator.AnalyteKey, specimenKbId, ct);
            if (lookup.Kind != Kb.KbLookupKind.Hit)
                await enrichmentRequest.RequestAsync(
                    indicator.AnalyteKey, specimenKbId, indicator.DisplayName, indicator.Id, userId,
                    origin: EnrichmentRequestOrigin.ManualEntry, ct: ct);
        }

        return SetRecordSpecimenResult.Success;
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

        var analyteKey = LabAnalyteNormalizer.Normalize(displayName);
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

        // Промах справочника по (показатель, источник) — ставим обогащение в очередь, тем же
        // единственным входом, что и распознавание документа (см. class doc выше). Гейт на
        // нерезолвленный источник и дедуп — внутри RequestAsync, не здесь.
        var lookup = await analyteKbLookup.LookupAsync(analyteKey, record.SpecimenKbId, ct);
        if (lookup.Kind != Kb.KbLookupKind.Hit)
            await enrichmentRequest.RequestAsync(
                analyteKey, record.SpecimenKbId, displayName, indicator.Id, userId,
                origin: EnrichmentRequestOrigin.ManualEntry, ct: ct);

        var specimenDisplayName = await db.GlobalSpecimensKb.AsNoTracking()
            .Where(s => s.Id == record.SpecimenKbId).Select(s => s.DisplayName).FirstOrDefaultAsync(ct);
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

        db.LabIndicators.Remove(indicator);
        await db.SaveChangesAsync(ct);
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

    private static IndicatorDto ToDto(DomainLabIndicator i, string? specimenDisplayName) => new(
        i.Id, i.AnalyteKey, i.DisplayName, i.Flag, i.RefSource, i.SpecimenKbId, specimenDisplayName, i.Position,
        i.ValueRaw, i.Unit, i.RefLowText, i.RefHighText, i.RefText, i.RecordDate, i.MedicalRecordId,
        i.ValueNumericText, i.KbAnalyteId, i.RawDisplayName);

    private static double? ParseNumeric(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;
    }
}
