using System.Globalization;
using System.Text.Json;
using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Messaging;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Attachments;
using FamilyHub.Modules.Medical.Enrichment;
using FamilyHub.Modules.Medical.Pipeline;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using DomainLabIndicator = FamilyHub.Domain.Entities.LabIndicator;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Шаги конвейера извлечения (ветка medicalrecords, редизайн v2) — декодирование → OCR/текстовый
/// разбор → структурирование → привязка к справочнику показателей → сохранение → суммаризация →
/// событие. Структура — зеркало MedicationEnrichmentProcessor (этап 4): выделенная Hangfire-
/// очередь "extraction" с одним воркером (LM Studio — один ноутбук за WireGuard, параллелить
/// нечего), AutomaticRetry только на настоящие сбои, ожидаемые исходы — Failed без ретрая.
///
/// v2: задача теперь на ЗАПИСЬ целиком — обрабатывает ПОСЛЕДОВАТЕЛЬНО все вложения записи, ещё не
/// распознанные (FileAttachment.ExtractedAt=null), не одно вложение по клику. Показатели из
/// разных файлов МЕРЖАТСЯ по ключу (AnalyteKey, SpecimenKbId) в существующий набор записи (upsert,
/// не blanket-delete) — повторный клик «Распознать» после добавления нового файла не стирает
/// результаты уже разобранных. Суммаризация — ОДИН проход по полному смерженному набору после
/// всех файлов, не по каждому файлу отдельно.
///
/// Каскад референса (см. IndicatorFlagCalculator, RefSource): бланк → фиксированный диапазон KB
/// (пол+возраст, identity rework) → расчёт локальной LLM по методике из KB (PatientReferenceCalculator)
/// → промах целиком (LabAnalyteEnrichmentRequestService ставит показатель в очередь обогащения
/// справочника; RecalculateIndicatorFlagsJob дозаполняет флаг задним числом, когда справочник
/// наполнится).
/// </summary>
[Queue("extraction")]
[AutomaticRetry(Attempts = MedicalDocumentExtractionProcessor.MaxAttempts, DelaysInSeconds = [60, 600, 3600])]
public class MedicalDocumentExtractionProcessor(
    AppDbContext db,
    AttachmentService attachments,
    IMedicalDocumentExtractor extractor,
    LabAnalyteKbLookupService kbLookup,
    LabAnalyteEnrichmentRequestService enrichmentRequest,
    OcrNameCorrector ocrNameCorrector,
    SpecimenResolver specimenResolver,
    PatientReferenceCalculator referenceCalculator,
    LabSummarizer summarizer,
    Kb.KbLookupService medicationKbLookup,
    VisitMedicationEnrichmentRequestService visitMedicationEnrichment,
    IPipelineConfigService pipelineConfig,
    IDomainEventPublisher publisher,
    ILogger<MedicalDocumentExtractionProcessor> logger)
{
    /// <summary>Должно совпадать с Attempts в [AutomaticRetry] на классе — на последней попытке
    /// catch-блок ниже переводит job в Failed сам, т.к. после неё Hangfire сдаётся молча и
    /// строка иначе осталась бы в Running навсегда, перманентно блокируя запись частичным
    /// уникальным индексом (Status IN (0,1)) — см. аудит, находка Critical #3.</summary>
    public const int MaxAttempts = 3;

    public async Task RunAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await db.MedicalDocumentExtractionJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
        {
            logger.LogWarning("MedicalDocumentExtractionJob {JobId} не найден — пропускаем.", jobId);
            return;
        }

        job.Attempts++;
        job.Status = EnrichmentJobStatus.Running;
        job.Stage = ExtractionStage.Decoding;
        job.StartedAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        try
        {
            var record = await db.MedicalRecords.FirstOrDefaultAsync(r => r.Id == job.MedicalRecordId, ct);
            if (record is null)
            {
                await FailAsync(job, "Мед-запись не найдена (возможно, удалена).", [], ct);
                return;
            }

            var pending = await db.FileAttachments.AsNoTracking()
                .Where(a => a.OwnerType == Domain.Enums.FileOwnerType.MedicalRecord && a.OwnerId == record.Id && a.ExtractedAt == null)
                .OrderBy(a => a.UploadedAt)
                .ToListAsync(ct);

            if (pending.Count == 0)
            {
                await FailAsync(job, "Нет новых вложений для распознавания — все уже распознаны.", [], ct);
                return;
            }

            job.TotalFiles = pending.Count;
            await db.SaveChangesAsync(ct);

            var results = new List<ExtractionResult>();
            var fileErrors = new List<string>();
            // Собираем id прочитанных вложений, но НЕ проставляем ExtractedAt здесь — раньше это
            // делалось отдельным ExecuteUpdateAsync прямо в цикле (собственный неявный коммит,
            // вне последующей транзакции с показателями/summary): крах процесса между этой
            // строкой и финальным SaveChangesAsync навсегда терял файл — ExtractedAt уже
            // проставлен, повторный клик «Распознать» видит его как уже обработанный и
            // пропускает, а извлечённые из него данные так и не сохранились (см. аудит,
            // находка Critical #2). Теперь пометка идёт одной транзакцией с результатом —
            // см. MarkAttachmentsExtractedAsync, вызывается из Process*Async/FailAsync ниже.
            var readAttachmentIds = new List<Guid>();

            foreach (var attachment in pending)
            {
                job.Stage = ExtractionStage.Decoding;
                await db.SaveChangesAsync(ct);

                var download = await attachments.GetDownloadAsync(attachment.Id, ct);
                if (download is null)
                {
                    fileErrors.Add($"{attachment.FileName}: вложение не найдено в хранилище");
                    job.ProcessedFiles++;
                    await db.SaveChangesAsync(ct);
                    continue;
                }

                byte[] bytes;
                await using (download.Value.Content)
                {
                    using var buffer = new MemoryStream();
                    await download.Value.Content.CopyToAsync(buffer, ct);
                    bytes = buffer.ToArray();
                }

                job.Stage = ExtractionStage.Ocr;
                await db.SaveChangesAsync(ct);

                var source = new DocumentSource(bytes, download.Value.ContentType, download.Value.FileName);
                var result = await extractor.ExtractAsync(source, record.Kind, ct);

                if (!result.Supported)
                    fileErrors.Add($"{attachment.FileName}: {result.FailureReason ?? "формат не поддержан распознаванием"}");
                else
                    results.Add(result);

                // Файл прочитан (успешно или с понятной причиной отказа) — не пытаемся снова при
                // следующем клике «Распознать»; необработанное исключение (ниже, вне цикла) не
                // доходит сюда, и файл останется в очереди на повтор. Сама пометка ExtractedAt —
                // ниже, одной транзакцией с результатом (см. комментарий у readAttachmentIds).
                readAttachmentIds.Add(attachment.Id);
                job.ProcessedFiles++;
                await db.SaveChangesAsync(ct);
            }

            job.Stage = ExtractionStage.Structuring;
            await db.SaveChangesAsync(ct);

            if (record.Kind == MedicalRecordKind.Analysis)
                await ProcessAnalysisAsync(job, record, results, fileErrors, readAttachmentIds, ct);
            else
                await ProcessVisitAsync(job, record, results, fileErrors, readAttachmentIds, ct);
        }
        catch (Exception ex)
        {
            job.Error = ex.Message;
            if (job.Attempts >= MaxAttempts)
            {
                // Это была последняя попытка [AutomaticRetry] — Hangfire сдаётся молча, дальше
                // никто не переведёт задачу в терминальный статус. Без этого строка осталась бы
                // в Running навсегда и частичный уникальный индекс (Status IN (0,1)) перманентно
                // блокировал бы повторную постановку в очередь для этой же записи.
                job.Status = EnrichmentJobStatus.Failed;
                job.CompletedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            logger.LogError(ex, "MedicalDocumentExtractionJob {JobId} упал на попытке {Attempts} — Hangfire повторит.", job.Id, job.Attempts);
            throw;
        }
    }

    private async Task ProcessAnalysisAsync(
        Domain.Entities.MedicalDocumentExtractionJob job, Domain.Entities.MedicalRecord record,
        List<ExtractionResult> results, List<string> fileErrors, List<Guid> readAttachmentIds, CancellationToken ct)
    {
        DateOnly? documentDate = null;
        string? suggestedTitle = null;
        string? doctor = null;
        var rawIndicators = new List<(ExtractedLabIndicator Dto, SpecimenDocumentResolution? Resolution)>();

        foreach (var result in results)
        {
            if (result.DocumentDate is not null) documentDate = result.DocumentDate;
            if (suggestedTitle is null && !string.IsNullOrWhiteSpace(result.SuggestedTitle)) suggestedTitle = result.SuggestedTitle;
            if (doctor is null && !string.IsNullOrWhiteSpace(result.Doctor)) doctor = result.Doctor;
            if (result.LabIndicators is null) continue;

            // Источник (биоматериал/исследование) резолвится один раз на файл экстрактором (см.
            // SpecimenResolver, LmStudioMedicalDocumentExtractor.ExtractAnalysisAsync) — здесь
            // только переносится на каждый показатель этого файла, ещё не сведён к Guid: секция
            // конкретного показателя (несколько панелей на одном бланке) может переопределить
            // document-level источник этого же файла, см. ниже.
            rawIndicators.AddRange(result.LabIndicators.Select(dto => (dto, result.SpecimenResolution)));
        }

        if (rawIndicators.Count == 0)
        {
            var reason = fileErrors.Count > 0 ? string.Join("; ", fileErrors) : "Не удалось распознать ни одного показателя.";
            await FailAsync(job, reason, readAttachmentIds, ct);
            return;
        }

        job.Stage = ExtractionStage.Linking;
        await db.SaveChangesAsync(ct);

        // Дата документа, если распозналась в бланке, — переопределяет дефолт "сегодня"
        // (проставленный при создании записи). Короткое название/врач — только если ещё не заданы
        // (не затираем то, что пользователь мог ввести вручную в форме создания).
        if (documentDate is not null) record.RecordDate = documentDate.Value;
        if (record.Title is null && suggestedTitle is not null) record.Title = suggestedTitle;
        if (record.Doctor is null && doctor is not null) record.Doctor = LabAnalyteNameCleaner.CleanPersonName(doctor);

        var recordId = record.Id;
        var ownerUserId = record.OwnerUserId;
        var recordDate = record.RecordDate;

        // Второй проход коррекции OCR — ДО нормализации/сопоставления со справочником: смешение
        // кириллицы/латиницы и КАПС в сыром имени снижают триграммную схожесть в pg_trgm-каскаде
        // ниже и порождают ложные промахи (см. OcrNameCorrector). Один батч-вызов на весь набор
        // показателей записи, не по одному на показатель. Необязательный шаг (§2 плана) —
        // выключен из админки означает пропуск LLM-вызова целиком, детерминированный cleaner
        // (LabAnalyteNameCleaner, ниже по конвейеру) продолжает работать без него.
        if (await pipelineConfig.IsEnabledAsync(PipelineCatalog.AnalysisExtraction, "ocr-correct", ct))
        {
            var correctedNames = await ocrNameCorrector.CorrectBatchAsync(
                rawIndicators.Select(x => x.Dto.Name).ToList(), ct);
            rawIndicators = rawIndicators
                .Select((x, i) => (x.Dto with { Name = correctedNames[i] }, x.Resolution))
                .ToList();
        }

        // Резолвим источник в Guid — секция (несколько панелей на одном бланке, см. SpecimenResolver)
        // побеждает над document-level контекстом того же файла, если модель явно перечислила этот
        // показатель в секции с другим источником. Кэш по (context, rawLabel, confidence) — один
        // запрос к справочнику на уникальную комбинацию, не на каждый показатель.
        var specimenKbIdCache = new Dictionary<(string? Context, string? RawLabel, double Confidence), Guid>();
        async Task<Guid> ResolveSpecimenKbIdAsync(string? context, string? rawLabel, double confidence)
        {
            var cacheKey = (context, rawLabel, confidence);
            if (specimenKbIdCache.TryGetValue(cacheKey, out var cached)) return cached;
            var resolved = await specimenResolver.ResolveKbIdAsync(context, confidence, rawLabel, ct);
            specimenKbIdCache[cacheKey] = resolved;
            return resolved;
        }

        var normalized = new List<(ExtractedLabIndicator Dto, string AnalyteKey, Guid SpecimenKbId)>();
        foreach (var (dto, resolution) in rawIndicators)
        {
            var analyteKey = LabAnalyteNormalizer.Normalize(dto.Name);
            if (analyteKey.Length == 0) continue;

            var section = resolution?.Sections.FirstOrDefault(s => s.IndicatorNames.Any(n =>
                string.Equals(LabAnalyteNormalizer.Normalize(n), analyteKey, StringComparison.Ordinal)));

            // Явное перечисление в секции — сильный сигнал самой модели по конкретному показателю,
            // не нуждается в отдельном сравнении с порогом confidence документа.
            var (context, rawLabel, confidence) = section is not null
                ? (section.Context, section.Context, 1.0)
                : (resolution?.Context, resolution?.RawLabel, resolution?.Confidence ?? 0);

            var specimenKbId = await ResolveSpecimenKbIdAsync(context, rawLabel, confidence);
            normalized.Add((dto, analyteKey, specimenKbId));
        }

        // Один Lookup на уникальную пару (имя, источник) — один и тот же показатель из одного и
        // того же источника может повторяться на одном бланке; тот же показатель из РАЗНОГО
        // источника (кровь/моча) ищется отдельно (пересборка enrich-пайплайна, см.
        // LabAnalyteKbLookupService).
        var lookups = new Dictionary<(string AnalyteKey, Guid SpecimenKbId), Kb.KbLookupResult>();
        foreach (var (_, analyteKey, specimenKbId) in normalized)
        {
            var key = (analyteKey, specimenKbId);
            if (!lookups.ContainsKey(key))
                lookups[key] = await kbLookup.LookupAsync(analyteKey, specimenKbId, ct);
        }

        var hitIds = lookups.Values.Where(l => l.Kind == Kb.KbLookupKind.Hit).Select(l => l.KbId!.Value).Distinct().ToList();
        Dictionary<Guid, (string PayloadJson, string DisplayName)> kbRows = hitIds.Count == 0
            ? []
            : await db.GlobalLabAnalytesKb.AsNoTracking()
                .Where(k => hitIds.Contains(k.Id))
                .Select(k => new { k.Id, k.PayloadJson, k.DisplayName })
                .ToDictionaryAsync(x => x.Id, x => (x.PayloadJson, x.DisplayName), ct);

        var (ageYears, sex) = await PatientIdentityResolver.ResolveAsync(db, record, ct);

        // Существующие показатели записи (из прошлых прогонов «Распознать» на этой же записи) —
        // upsert по (AnalyteKey, SpecimenKbId), НЕ blanket-delete: повторный клик с новым файлом не
        // должен стирать результаты уже распознанных ранее файлов той же записи.
        var existing = await db.LabIndicators.Where(i => i.MedicalRecordId == recordId).ToListAsync(ct);
        var existingByKey = existing.ToDictionary(i => (i.AnalyteKey, i.SpecimenKbId));
        var nextPosition = existing.Count == 0 ? 0 : existing.Max(i => i.Position) + 1;

        foreach (var (dto, analyteKey, specimenKbId) in normalized)
        {
            var lookup = lookups[(analyteKey, specimenKbId)];
            var kbAnalyteId = lookup.Kind == Kb.KbLookupKind.Hit ? lookup.KbId : null;
            var kbRow = kbAnalyteId is not null && kbRows.TryGetValue(kbAnalyteId.Value, out var row) ? row : ((string PayloadJson, string DisplayName)?)null;

            KbReferenceRange? kbFallback = kbRow is null
                ? null
                : IndicatorFlagCalculator.PickBestRange(LabAnalyteKbPayload.ParseRefRanges(kbRow.Value.PayloadJson), ageYears, sex);

            var (flag, refSource, effLow, effHigh) = IndicatorFlagCalculator.Calculate(dto, kbFallback, ageYears, sex);

            // Каскад шаг 3: KB-запись есть, фиксированный диапазон не подошёл под пациента, но
            // есть словесная методика расчёта — просим локальную LLM посчитать под конкретного
            // пациента (возраст/пол), в единице измерения бланка.
            if (refSource == RefSource.None && kbRow is not null &&
                await pipelineConfig.IsEnabledAsync(PipelineCatalog.AnalysisExtraction, "patient-reference", ct))
            {
                var instructions = LabAnalyteKbPayload.ParseCalculationInstructions(kbRow.Value.PayloadJson);
                if (!string.IsNullOrWhiteSpace(instructions))
                {
                    var calculated = await referenceCalculator.CalculateAsync(dto.Name, instructions, ageYears, sex, dto.Unit, ct);
                    if (calculated is not null)
                    {
                        effLow = calculated.Value.Low;
                        effHigh = calculated.Value.High;
                        flag = IndicatorFlagCalculator.ApplyCalculatedRange(dto.Value, effLow, effHigh);
                        refSource = RefSource.KbCalculated;
                    }
                }
            }

            var key = (analyteKey, specimenKbId);
            if (!existingByKey.TryGetValue(key, out var entity))
            {
                entity = new DomainLabIndicator
                {
                    Id = Guid.NewGuid(),
                    MedicalRecordId = recordId,
                    OwnerUserId = ownerUserId,
                    AnalyteKey = analyteKey,
                    SpecimenKbId = specimenKbId,
                    Position = nextPosition++,
                    CreatedAt = DateTime.UtcNow,
                };
                db.LabIndicators.Add(entity);
                existingByKey[key] = entity;
            }

            // Каноническое имя из справочника при попадании — сырое (очищенное от нумерации/КАПС)
            // имя с бланка остаётся рядом подсказкой (пересборка enrich-пайплайна, см.
            // LabIndicator.RawDisplayName). Промах — само очищенное имя с бланка становится
            // отображаемым, RawDisplayName пуст (нечего подсказывать, DisplayName и есть бланк).
            var cleanedFromForm = LabAnalyteNameCleaner.Clean(dto.Name);
            if (kbRow is not null)
            {
                entity.DisplayName = kbRow.Value.DisplayName;
                entity.RawDisplayName = string.Equals(kbRow.Value.DisplayName, cleanedFromForm, StringComparison.Ordinal)
                    ? null : dto.Name;
            }
            else
            {
                entity.DisplayName = cleanedFromForm;
                entity.RawDisplayName = string.Equals(cleanedFromForm, dto.Name, StringComparison.Ordinal) ? null : dto.Name;
            }

            entity.RecordDate = recordDate;
            entity.KbAnalyteId = kbAnalyteId;
            entity.Flag = flag;
            entity.RefSource = refSource;
            entity.ValueRaw = dto.Value;
            entity.ValueNumericText = TryFormatNumeric(dto.Value);
            entity.Unit = dto.Unit;
            entity.RefLowText = effLow?.ToString(CultureInfo.InvariantCulture);
            entity.RefHighText = effHigh?.ToString(CultureInfo.InvariantCulture);
            entity.RefText = dto.RefText;

            // Промах/неуверенный кандидат — ставим показатель в очередь обогащения справочника.
            // Дедуп на уровне БД + жёсткий гейт на нерезолвленный источник — оба внутри
            // LabAnalyteEnrichmentRequestService.RequestAsync (единственная точка входа).
            if (lookup.Kind != Kb.KbLookupKind.Hit)
                await enrichmentRequest.RequestAsync(analyteKey, specimenKbId, entity.DisplayName, null, ownerUserId, ct);
        }

        job.Stage = ExtractionStage.Summarizing;
        await db.SaveChangesAsync(ct);

        // ОДИН проход суммаризатора по ПОЛНОМУ смерженному набору показателей записи — не по
        // каждому файлу отдельно, иначе summary не видел бы показатели, распознанные раньше.
        // Необязательный шаг (§2 плана) — выключен из админки означает отсутствие сводки, сами
        // показатели уже сохранены и не зависят от неё.
        var allIndicators = existingByKey.Values.ToList();
        record.SummaryJson = null;
        if (await pipelineConfig.IsEnabledAsync(PipelineCatalog.AnalysisExtraction, "record-summary", ct))
        {
            var summarized = await summarizer.SummarizeAsync(allIndicators, ct);
            record.SummaryJson = summarized.Success && summarized.Summary is not null
                ? JsonSerializer.Serialize(summarized.Summary)
                : null;
        }
        record.ExtractionStatus = ExtractionStatus.Ready;

        var deviationCount = allIndicators.Count(i => i.Flag is IndicatorFlag.Low or IndicatorFlag.High or IndicatorFlag.Critical);
        job.IndicatorCount = allIndicators.Count;
        job.Status = EnrichmentJobStatus.Completed;
        job.Error = fileErrors.Count > 0 ? string.Join("; ", fileErrors) : null;
        job.CompletedAt = DateTime.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // ExtractedAt проставляется здесь же — одной транзакцией с показателями/summary (см.
        // комментарий у readAttachmentIds в RunAsync): либо оба сохраняются, либо оба откатываются.
        await MarkAttachmentsExtractedAsync(readAttachmentIds, ct);
        await publisher.PublishAsync(new MedicalDocumentExtractedEvent(job.Id, recordId, ownerUserId, allIndicators.Count, deviationCount), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation(
            "MedicalDocumentExtractionJob {JobId}: распознано {Count} показателей ({Deviations} отклонений) из {Files} файлов.",
            job.Id, allIndicators.Count, deviationCount, results.Count);
    }

    private async Task ProcessVisitAsync(
        Domain.Entities.MedicalDocumentExtractionJob job, Domain.Entities.MedicalRecord record,
        List<ExtractionResult> results, List<string> fileErrors, List<Guid> readAttachmentIds, CancellationToken ct)
    {
        var conclusion = results.Select(r => r.Conclusion).FirstOrDefault(c => c is not null);
        if (conclusion is null)
        {
            var reason = fileErrors.Count > 0 ? string.Join("; ", fileErrors) : "Не удалось распознать заключение врача.";
            await FailAsync(job, reason, readAttachmentIds, ct);
            return;
        }

        // Чистка названий назначенных препаратов (нумерация/эхо-индекс/КАПС) — до того, как
        // заключение уйдёт и в ExtractedDataJson (сырой текст, который видит пользователь), и в
        // очередь обогащения справочника медикаментов ниже (пересборка enrich-пайплайна, §5 плана).
        if (conclusion.PrescribedMedications is { Count: > 0 })
        {
            conclusion = conclusion with
            {
                PrescribedMedications = conclusion.PrescribedMedications
                    .Select(m => m with { Name = LabAnalyteNameCleaner.Clean(m.Name) })
                    .ToList(),
            };
        }

        var documentDate = results.Select(r => r.DocumentDate).FirstOrDefault(d => d is not null);
        var suggestedTitle = results.Select(r => r.SuggestedTitle).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        var doctor = results.Select(r => r.Doctor).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        if (documentDate is not null) record.RecordDate = documentDate.Value;
        if (record.Title is null && suggestedTitle is not null) record.Title = suggestedTitle;
        if (record.Doctor is null && doctor is not null) record.Doctor = LabAnalyteNameCleaner.CleanPersonName(doctor);

        // Назначенные препараты — сверяем со справочником медикаментов (тот же, что у аптечки);
        // промах ставит обогащение в очередь (UX-редизайн, см. VisitMedicationEnrichmentRequestService).
        // Ссылка на найденную запись справочника НЕ сохраняется здесь — резолвится на чтение
        // (ExtractionQueryService.GetConclusionAsync), чтобы не требовать бэкофилла, когда
        // обогащение завершится уже после первого просмотра заключения.
        foreach (var med in conclusion.PrescribedMedications ?? [])
        {
            var normalizedName = MedicationNameNormalizer.Normalize(med.Name);
            if (normalizedName.Length == 0) continue;

            var lookup = await medicationKbLookup.LookupAsync(normalizedName, ct);
            if (lookup.Kind != Kb.KbLookupKind.Hit)
                await visitMedicationEnrichment.RequestAsync(normalizedName, med.Name, record.Id, record.OwnerUserId, ct);
        }

        record.ExtractedDataJson = JsonSerializer.Serialize(conclusion);
        record.ExtractionStatus = ExtractionStatus.Ready;

        job.IndicatorCount = 0;
        job.Status = EnrichmentJobStatus.Completed;
        job.Error = fileErrors.Count > 0 ? string.Join("; ", fileErrors) : null;
        job.CompletedAt = DateTime.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await MarkAttachmentsExtractedAsync(readAttachmentIds, ct);
        await publisher.PublishAsync(new MedicalDocumentExtractedEvent(job.Id, record.Id, record.OwnerUserId, 0, 0), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation("MedicalDocumentExtractionJob {JobId}: заключение врача распознано.", job.Id);
    }

    /// <summary>Проставляет FileAttachment.ExtractedAt для успешно прочитанных вложений — вызывается
    /// либо внутри финальной транзакции успеха (см. Process*Async выше), либо здесь, при отказе:
    /// в обоих случаях это одна транзакция с решением по задаче, а не отдельный неявный коммит
    /// посреди цикла (см. аудит, находка Critical #2).</summary>
    private async Task MarkAttachmentsExtractedAsync(IReadOnlyList<Guid> attachmentIds, CancellationToken ct)
    {
        if (attachmentIds.Count == 0) return;
        await db.FileAttachments.Where(a => attachmentIds.Contains(a.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ExtractedAt, DateTime.UtcNow), ct);
    }

    private async Task FailAsync(
        Domain.Entities.MedicalDocumentExtractionJob job, string reason,
        List<Guid> readAttachmentIds, CancellationToken ct)
    {
        job.Status = EnrichmentJobStatus.Failed;
        job.Error = reason;
        job.CompletedAt = DateTime.UtcNow;

        if (readAttachmentIds.Count > 0)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await MarkAttachmentsExtractedAsync(readAttachmentIds, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("MedicalDocumentExtractionJob {JobId}: {Reason}", job.Id, reason);
    }

    private static string? TryFormatNumeric(string value)
    {
        var normalized = value.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d.ToString(CultureInfo.InvariantCulture)
            : null;
    }
}
