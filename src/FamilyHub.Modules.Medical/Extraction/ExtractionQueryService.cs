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
    Kb.KbAnalyteCatalogService analyteCatalog, IMedicalAuditWriter audit, LabSummarizer summarizer)
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

    /// <summary>Последнее значение по каждому (показатель, источник) среди СВОИХ записей
    /// пользователя (владелец) — расшаренные чужие записи сюда не входят, "мои показатели" в
    /// буквальном смысле. SpecimenKbId — часть ключа группировки (пересборка enrich-пайплайна):
    /// лейкоциты крови и мочи, а также два разных источника вне общего набора (ЭКГ, УЗИ) не
    /// должны схлопнуться в одну строку.</summary>
    public async Task<List<MyIndicatorSummary>> GetMyIndicatorsAsync(Guid userId, CancellationToken ct = default)
    {
        var all = await db.LabIndicators.AsNoTracking()
            .Where(i => i.OwnerUserId == userId)
            .ToListAsync(ct);

        var latest = all
            .GroupBy(i => (i.AnalyteKey, i.SpecimenKbId))
            .Select(g => g.OrderByDescending(i => i.RecordDate).First())
            .ToList();

        var specimenNames = await ResolveSpecimenNamesAsync(latest.Select(i => i.SpecimenKbId), ct);
        return latest
            .Select(i => new MyIndicatorSummary(
                i.AnalyteKey, i.DisplayName, i.SpecimenKbId, specimenNames.GetValueOrDefault(i.SpecimenKbId),
                i.ValueRaw, i.Unit, i.Flag, i.RecordDate))
            .OrderBy(s => s.DisplayName)
            .ToList();
    }

    public async Task<List<IndicatorHistoryPoint>> GetHistoryAsync(
        Guid userId, string analyteKey, Guid specimenKbId, CancellationToken ct = default)
    {
        var items = await db.LabIndicators.AsNoTracking()
            .Where(i => i.OwnerUserId == userId && i.AnalyteKey == analyteKey && i.SpecimenKbId == specimenKbId)
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
    /// строго "свои" — этот работает и для расшаренной чужой записи). Двойной фильтр обязателен:
    /// владелец записи (все точки тренда — один и тот же человек, идентичность не размывается) И
    /// видимость КАЖДОЙ точки лично зрителю — без второго условия тренд по одной расшаренной записи
    /// обошёл бы точечное скрытие MedicalRecordHidden (L2), см. риск Р5 плана редизайна.</summary>
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
                && visibleIds.Contains(i.MedicalRecordId))
            .OrderBy(i => i.RecordDate)
            .ToListAsync(ct);

        return items.Select(i => new IndicatorHistoryPoint(i.RecordDate, i.ValueRaw, i.ValueNumericText, i.Flag, i.MedicalRecordId)).ToList();
    }

    /// <summary>Правка показателя вручную (ошибка OCR) — только владелец мед-записи. Ref-поля,
    /// присланные в запросе, становятся новым "референсом с бланка" (RefSource.Blank) — ручная
    /// правка семантически заменяет то, что распознала модель, тем же приоритетом, что и печатный
    /// бланк; KB/расчётный каскад заново не гоняется (пользователь правит конкретные цифры, а не
    /// просит переопределить справочником). Flag пересчитывается тем же компаратором, что и при
    /// автораспознавании — не дублируем пороговую логику.</summary>
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

        // Источник (пересборка enrich-пайплайна) — должен существовать в общем справочнике
        // (сентинел "не определено" — тоже валидная строка, разрешён). Ручная правка НИКОГДА не
        // ставит обогащение в очередь (жёсткое требование) — только перепривязка к уже
        // существующей строке KB, даже если выбор явно не подходит показателю.
        if (!await db.GlobalSpecimensKb.AnyAsync(s => s.Id == request.SpecimenKbId, ct))
            return UpdateIndicatorResult.NotFound;

        // Уникальный индекс (MedicalRecordId, AnalyteKey, SpecimenKbId) — правка могла увести
        // показатель на пару, уже занятую другой строкой этой же записи.
        var conflict = await db.LabIndicators.AnyAsync(i =>
            i.Id != indicatorId && i.MedicalRecordId == indicator.MedicalRecordId &&
            i.AnalyteKey == analyteKey && i.SpecimenKbId == request.SpecimenKbId, ct);
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
        indicator.SpecimenKbId = request.SpecimenKbId;
        indicator.ValueRaw = request.ValueRaw;
        indicator.ValueNumericText = ParseNumeric(request.ValueRaw)?.ToString(CultureInfo.InvariantCulture);
        indicator.Unit = request.Unit;
        indicator.RefLowText = effLow?.ToString(CultureInfo.InvariantCulture);
        indicator.RefHighText = effHigh?.ToString(CultureInfo.InvariantCulture);
        indicator.RefText = refText;
        indicator.Flag = flag;
        indicator.RefSource = refSource;

        await db.SaveChangesAsync(ct);
        return UpdateIndicatorResult.Success;
    }

    /// <summary>Ручное добавление показателя (UX-редизайн) — тот же путь расчёта флага, что и
    /// правка: RefSource.Blank, KB-каскад заново не гоняется (пользователь вводит конкретные
    /// цифры руками, не просит распознать заново). Position — в конец текущего списка записи.</summary>
    public async Task<(CreateIndicatorResult Result, IndicatorDto? Item)> CreateIndicatorAsync(
        Guid recordId, Guid userId, CreateIndicatorRequest request, CancellationToken ct = default)
    {
        var record = await db.MedicalRecords.AsNoTracking()
            .Where(r => r.Id == recordId).Select(r => new { r.Id, r.OwnerUserId }).FirstOrDefaultAsync(ct);
        if (record is null) return (CreateIndicatorResult.NotFound, null);
        if (record.OwnerUserId != userId) return (CreateIndicatorResult.Forbidden, null);

        var displayName = request.DisplayName.Trim();
        if (displayName.Length == 0) return (CreateIndicatorResult.NotFound, null);

        var analyteKey = LabAnalyteNormalizer.Normalize(displayName);
        if (analyteKey.Length == 0) return (CreateIndicatorResult.NotFound, null);

        // Источник должен существовать в общем справочнике (сентинел "не определено" — тоже
        // валидная строка). Ручное добавление НЕ ставит обогащение в очередь (жёсткое требование).
        var specimenRow = await db.GlobalSpecimensKb.AsNoTracking()
            .Where(s => s.Id == request.SpecimenKbId).Select(s => new { s.Id, s.DisplayName }).FirstOrDefaultAsync(ct);
        if (specimenRow is null) return (CreateIndicatorResult.NotFound, null);

        var conflict = await db.LabIndicators.AnyAsync(i =>
            i.MedicalRecordId == recordId && i.AnalyteKey == analyteKey && i.SpecimenKbId == request.SpecimenKbId, ct);
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

        var recordDate = await db.MedicalRecords.Where(r => r.Id == recordId).Select(r => r.RecordDate).FirstAsync(ct);

        var indicator = new DomainLabIndicator
        {
            Id = Guid.NewGuid(),
            MedicalRecordId = recordId,
            RecordDate = recordDate,
            OwnerUserId = userId,
            AnalyteKey = analyteKey,
            DisplayName = displayName,
            Flag = flag,
            RefSource = refSource,
            SpecimenKbId = specimenRow.Id,
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

        return (CreateIndicatorResult.Success, ToDto(indicator, specimenRow.DisplayName));
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
