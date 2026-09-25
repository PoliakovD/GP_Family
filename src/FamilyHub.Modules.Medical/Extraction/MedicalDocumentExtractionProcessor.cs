using System.Globalization;
using System.Text.Json;
using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.LmStudio;
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
    QualitativeNormJudge qualitativeJudge,
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

    /// <summary>Потолок числа показателей в файле, при котором ещё имеет смысл переписать их
    /// имена по AnalyteSubjectResolver — премисса шага "весь этот файл посвящён одному объекту
    /// поиска" неправдоподобна для бланка-панели с большим числом строк, что бы ни ответила
    /// модель на конкретный файл.</summary>
    private const int MaxIndicatorsForSubjectRewrite = 5;

    public async Task RunAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await db.MedicalDocumentExtractionJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
        {
            logger.LogWarning("MedicalDocumentExtractionJob {JobId} не найден — пропускаем.", jobId);
            return;
        }

        // Ambient-контекст для живого потока "мыслей" (план) — на весь остаток метода: любой
        // вложенный вызов LmStudioJsonClient.ExtractJsonAsync ниже (напрямую или через
        // LmStudioMedicalDocumentExtractor/SpecimenResolver/AnalysisTitleGenerator/
        // AnalyteSubjectResolver/OcrNameCorrector/PatientReferenceCalculator/LabSummarizer)
        // подхватит его сам, без передачи через сигнатуры этих методов — см. LmStudioThinkingContext.
        using var _ = LmStudioThinkingContext.Begin(LlmJobKind.Extraction, job.Id);

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
                // Запись удалена — некому уведомлять (OwnerUserId неизвестен), FailAsync ниже без
                // record публикацию и не пытается.
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
                // KindIsAutoDetected (батч-загрузка) — передаём null, экстрактор сам определит вид
                // документа (DocumentKindClassifier) и вернёт его в result.Kind; итоговый Kind
                // записи считается большинством голосов ПОСЛЕ цикла (см. ниже). Обычная форма
                // создания уже знает вид — передаём его прямо, классификатор не вызывается.
                var result = await extractor.ExtractAsync(source, record.KindIsAutoDetected ? null : record.Kind, ct);

                // Технический сбой (LM Studio недоступен) — НЕ проставляем ExtractedAt на
                // непрочитанном файле (readAttachmentIds ниже не пополняется) и пробрасываем
                // исключение: catch в RunAsync запустит Hangfire-ретрай по расписанию вместо того,
                // чтобы навсегда похоронить файл как "распознанный" (см. план, часть 1).
                if (result.IsTransientFailure)
                    throw new LmStudioUnavailableException(result.FailureReason ?? "Локальный сервер распознавания недоступен.");

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

            // Батч-загрузка (KindIsAutoDetected) — итоговый вид записи считается большинством
            // голосов по results[].Kind (каждый УСПЕШНО прочитанный файл проголосовал за вид,
            // которым его фактически разобрал DocumentKindClassifier внутри extractor.ExtractAsync
            // выше). Ничья/пусто (все файлы не читаются) → Analysis, тот же дефолт-по-умолчанию,
            // что и в самом классификаторе. В батче файл всегда один — голосование здесь чисто
            // страховка для записи с несколькими вложениями. Флаг снимается сразу: повторный клик
            // «Распознать» на этой же записи (новый файл добавлен позже) больше не переопределяет
            // уже определённый вид — та же логика "не затирать то, что уже решено", что у
            // Title/Doctor/SpecimenKbId ниже по конвейеру.
            if (record.KindIsAutoDetected && results.Count > 0)
            {
                record.Kind = results
                    .GroupBy(r => r.Kind)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key == MedicalRecordKind.Analysis ? 0 : 1)
                    .First().Key;
                record.KindIsAutoDetected = false;
            }

            job.Stage = ExtractionStage.Structuring;
            await db.SaveChangesAsync(ct);

            if (record.Kind == MedicalRecordKind.Analysis)
                await ProcessAnalysisAsync(job, record, results, fileErrors, readAttachmentIds, ct);
            else
                await ProcessVisitAsync(job, record, results, fileErrors, readAttachmentIds, ct);
        }
        catch (LmStudioUnavailableException ex)
        {
            // ИИ недоступен (ноутбук выключен/спит, туннель упал) — это не отказ задачи, а ожидание.
            // Раньше исключение уходило в [AutomaticRetry] (60с/10мин/1ч), после чего задача становилась
            // красным Failed, и пользователь видел «не удалось распознать». Теперь задача остаётся
            // Pending с флагом WaitingForAi, попытка не тратится, запись по-прежнему «в процессе», а
            // LmStudioRecoverySweepJob запускает её, как только сервер снова отвечает. Ничего не
            // пробрасываем — Hangfire-повтор здесь только сжёг бы попытки впустую.
            job.Status = EnrichmentJobStatus.Pending;
            job.Stage = ExtractionStage.Queued;
            job.WaitingForAi = true;
            job.Attempts = Math.Max(0, job.Attempts - 1);
            job.ProcessedFiles = 0;
            job.Error = null;
            job.CurrentThought = null;
            await db.SaveChangesAsync(ct);
            logger.LogWarning(ex, "MedicalDocumentExtractionJob {JobId}: ИИ недоступен — задача ждёт его в очереди.", job.Id);
            return;
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
                // Помечаем ТОЛЬКО технический сбой (LM Studio так и не ответил за все попытки) —
                // по этому флагу LmStudioRecoverySweepJob находит задачи, которые стоит вернуть в
                // очередь, когда сервер снова станет доступен (см. план, часть 1).
                job.IsTransientFailure = ex is LmStudioUnavailableException;
                // См. комментарий у FailAsync — та же причина: запись должна сама отражать
                // терминальный отказ, не только job-таблица.
                await db.MedicalRecords.Where(r => r.Id == job.MedicalRecordId)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.ExtractionStatus, ExtractionStatus.Failed), ct);

                // record — переменная из try-блока, здесь недоступна, поэтому владельца/вид читаем
                // отдельно тем же MedicalRecordId, что и в ExecuteUpdateAsync выше.
                var owner = await db.MedicalRecords.AsNoTracking()
                    .Where(r => r.Id == job.MedicalRecordId)
                    .Select(r => new { r.OwnerUserId, r.Kind })
                    .FirstOrDefaultAsync(ct);
                if (owner is not null)
                    await PublishFailureAsync(
                        job, owner.OwnerUserId, owner.Kind == MedicalRecordKind.DoctorVisit,
                        job.Error ?? "Не удалось распознать документ.", ct);
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

        // Группа на файл (не плоский список, как раньше) — связь показателя с конкретным файлом
        // нужна для уточнения родового названия по разделу "Оказанные услуги" (см.
        // AnalyteSubjectResolver: субъект резолвится на файл целиком, не на отдельный показатель) и
        // для разведения коллизий ключа между файлами, когда субъект не определился (см.
        // AnalyteKeyDisambiguator ниже).
        var fileGroups = results
            .Where(r => r.LabIndicators is { Count: > 0 })
            .Select(r => (FileGroupId: Guid.NewGuid(), Result: r))
            .ToList();

        foreach (var result in results)
        {
            if (result.DocumentDate is not null) documentDate = result.DocumentDate;
            if (suggestedTitle is null && !string.IsNullOrWhiteSpace(result.SuggestedTitle)) suggestedTitle = result.SuggestedTitle;
            if (doctor is null && !string.IsNullOrWhiteSpace(result.Doctor)) doctor = result.Doctor;
        }

        if (fileGroups.Count == 0)
        {
            var reason = fileErrors.Count > 0 ? string.Join("; ", fileErrors) : "Не удалось распознать ни одного показателя.";
            await FailAsync(job, reason, readAttachmentIds, ct, record);
            return;
        }

        var rawIndicators = fileGroups
            .SelectMany(fg => fg.Result.LabIndicators!.Select(dto => (Dto: dto, fg.FileGroupId)))
            .ToList();

        job.Stage = ExtractionStage.Linking;
        await db.SaveChangesAsync(ct);

        // Дата документа, если распозналась в бланке, — переопределяет дефолт "сегодня"
        // (проставленный при создании записи). Короткое название/врач — только если ещё не заданы
        // (не затираем то, что пользователь мог ввести вручную в форме создания).
        if (documentDate is not null) record.RecordDate = documentDate.Value;
        if (record.Title is null && suggestedTitle is not null) record.Title = suggestedTitle;
        if (record.Doctor is null && doctor is not null) record.Doctor = LabAnalyteNameCleaner.CleanPersonName(doctor);

        // Источник — атрибут ВСЕЙ записи, не отдельного показателя (заметка 1): смешанный бланк
        // (кровь+моча) пользователь разделяет вручную на две записи, не конвейер посекционно. Уже
        // резолвленный источник (ручной выбор пользователя или прошлый прогон «Распознать») НЕ
        // переопределяется — та же логика, что у Title/Doctor выше. Среди файлов этого прогона
        // берём резолюцию с наибольшей уверенностью модели.
        if (record.SpecimenKbId == Domain.Entities.SpecimenContextIds.Unresolved)
        {
            var bestResolution = results
                .Select(r => r.SpecimenResolution)
                .Where(r => r is not null)
                .OrderByDescending(r => r!.Confidence)
                .FirstOrDefault();

            if (bestResolution is not null)
            {
                record.SpecimenKbId = await specimenResolver.ResolveKbIdAsync(
                    bestResolution.Context, bestResolution.Confidence, bestResolution.RawLabel, ct);

                // Источник вида "мазок"/"соскоб" без указанной локализации (заметка 2) — модель
                // намеренно не регистрирует его как context; кладём подсказку для UI, которая
                // попросит пользователя уточнить, откуда именно.
                if (record.SpecimenKbId == Domain.Entities.SpecimenContextIds.Unresolved &&
                    !string.IsNullOrWhiteSpace(bestResolution.NeedsSite))
                    record.SpecimenHint = bestResolution.NeedsSite;
            }
        }
        var recordSpecimenKbId = record.SpecimenKbId;

        var recordId = record.Id;
        var ownerUserId = record.OwnerUserId;
        var recordDate = record.RecordDate;
        var familyDependentId = record.FamilyDependentId;
        var targetUserId = record.TargetUserId;

        // Существующие показатели записи (из прошлых прогонов «Распознать» на этой же записи) —
        // загружаем ЗДЕСЬ, а не только перед финальным сохранением: набор нужен уже
        // AnalyteKeyDisambiguator ниже — коллизия ключа возникает не только МЕЖДУ файлами одного
        // прогона, но и между новым файлом и УЖЕ СОХРАНЁННЫМ показателем прошлого прогона (обычная
        // картина — «Распознать» нажимается по одному файлу за раз, см. §доп. плана). Без этого
        // второй по счёту файл посева, распознанный ОТДЕЛЬНЫМ кликом, молча переписал бы первый —
        // ровно баг, который весь этот план должен был устранить.
        var existing = await db.LabIndicators.Where(i => i.MedicalRecordId == recordId).ToListAsync(ct);
        var existingByKey = existing.ToDictionary(i => (i.AnalyteKey, i.SpecimenKbId));
        var nextPosition = existing.Count == 0 ? 0 : existing.Max(i => i.Position) + 1;
        var existingAnalyteKeysForRecord = existing
            .Where(i => i.SpecimenKbId == recordSpecimenKbId)
            .Select(i => i.AnalyteKey)
            .ToHashSet(StringComparer.Ordinal);

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
                .Select((x, i) => (x.Dto with { Name = correctedNames[i] }, x.FileGroupId))
                .ToList();
        }

        // Уточнение родового названия по разделу "Оказанные услуги" (см. AnalyteSubjectResolver) —
        // применяется ко ВСЕМ показателям файла целиком: резолвер уже проверил, что документ
        // посвящён одному объекту, разбирать показатели файла по отдельности не нужно. Потолок
        // MaxIndicatorsForSubjectRewrite — премисса "весь файл про один объект" не выдержана для
        // бланков-панелей с большим числом строк, что бы ни ответила модель на этот файл.
        var subjectByFileGroup = fileGroups
            .Where(fg => !string.IsNullOrWhiteSpace(fg.Result.SubjectResolution?.Subject))
            .ToDictionary(fg => fg.FileGroupId, fg => fg.Result.SubjectResolution!);

        if (subjectByFileGroup.Count > 0)
        {
            var countByFileGroup = rawIndicators
                .GroupBy(x => x.FileGroupId)
                .ToDictionary(g => g.Key, g => g.Count());

            rawIndicators = rawIndicators
                .Select(x =>
                {
                    if (!subjectByFileGroup.TryGetValue(x.FileGroupId, out var subject)) return x;
                    if (countByFileGroup[x.FileGroupId] > MaxIndicatorsForSubjectRewrite) return x;

                    var normalizedSubject = LabAnalyteNormalizer.NormalizeAnalyteKey(subject.Subject);
                    if (LabAnalyteNormalizer.NormalizeAnalyteKey(x.Dto.Name) == normalizedSubject) return x; // уточнять нечего

                    return (x.Dto with { Name = $"{subject.Subject} ({x.Dto.Name})" }, x.FileGroupId);
                })
                .ToList();
        }

        // Базовый ключ (без разведения коллизий) — то, что реально ищется в справочнике/ставится в
        // очередь обогащения ниже (§4 плана: суффикс — техническая деталь ХРАНЕНИЯ, KB не должен
        // получить в качестве имени "бактериальные микроорганизмы файл 2").
        var withBaseKey = rawIndicators
            .Select(x => (x.Dto, x.FileGroupId, BaseAnalyteKey: LabAnalyteNormalizer.NormalizeAnalyteKey(x.Dto.Name)))
            .Where(x => x.BaseAnalyteKey.Length > 0)
            .ToList();

        // Запасной вариант (не LLM) — разводим оставшиеся коллизии, когда субъект не определился
        // (модель не уверена, шаг выключен, LM Studio недоступен), а базовый ключ всё равно
        // совпал — МЕЖДУ файлами этого прогона (см. AnalyteKeyDisambiguator) И между новым файлом
        // и УЖЕ СОХРАНЁННЫМ показателем прошлого прогона (existingAnalyteKeysForRecord — «Распознать»
        // обычно нажимается по одному файлу за раз, коллизия с прошлым прогоном — типичный случай,
        // не редкий). Без второго — второй по счёту файл посева, распознанный отдельным кликом,
        // молча переписал бы первый (upsert по (AnalyteKey, SpecimenKbId) ниже). Кандидаты с
        // одинаковым FileGroupId (повтор строки ОДНОГО бланка) уже схлопнуты DeduplicateByName
        // в экстракторе, разводке не подлежат.
        var disambiguation = AnalyteKeyDisambiguator.Disambiguate(
            withBaseKey.Select(x => new AnalyteKeyDisambiguator.Candidate(x.BaseAnalyteKey, x.FileGroupId)).ToList(),
            existingAnalyteKeysForRecord);

        // AnalyteKey — ключ ХРАНЕНИЯ (с суффиксом при коллизии), LookupKey — ключ ПОИСКА в
        // справочнике (всегда базовый, без суффикса), DisplaySuffix — хвост для DisplayName/
        // RawDisplayName, когда показатель разведён (см. AnalyteKeyDisambiguator class doc).
        var normalized = withBaseKey
            .Select(x =>
            {
                var hasSuffix = disambiguation.TryGetValue((x.BaseAnalyteKey, x.FileGroupId), out var d);
                return (
                    Dto: x.Dto,
                    AnalyteKey: hasSuffix ? d!.AnalyteKey : x.BaseAnalyteKey,
                    SpecimenKbId: recordSpecimenKbId,
                    LookupKey: x.BaseAnalyteKey,
                    DisplaySuffix: hasSuffix ? d!.DisplaySuffix : null);
            })
            .ToList();

        // Один Lookup на уникальную пару (имя, источник) — один и тот же показатель из одного и
        // того же источника может повторяться на одном бланке; тот же показатель из РАЗНОГО
        // источника (кровь/моча) ищется отдельно (пересборка enrich-пайплайна, см.
        // LabAnalyteKbLookupService).
        var lookups = new Dictionary<(string LookupKey, Guid SpecimenKbId), Kb.KbLookupResult>();
        foreach (var item in normalized)
        {
            var key = (item.LookupKey, item.SpecimenKbId);
            if (!lookups.ContainsKey(key))
                lookups[key] = await kbLookup.LookupAsync(item.LookupKey, item.SpecimenKbId, ct);
        }

        var hitIds = lookups.Values.Where(l => l.Kind == Kb.KbLookupKind.Hit).Select(l => l.KbId!.Value).Distinct().ToList();
        Dictionary<Guid, (string PayloadJson, string DisplayName)> kbRows = hitIds.Count == 0
            ? []
            : await db.GlobalLabAnalytesKb.AsNoTracking()
                .Where(k => hitIds.Contains(k.Id))
                .Select(k => new { k.Id, k.PayloadJson, k.DisplayName })
                .ToDictionaryAsync(x => x.Id, x => (x.PayloadJson, x.DisplayName), ct);

        var (ageYears, sex) = await PatientIdentityResolver.ResolveAsync(db, record, ct);

        foreach (var (dto, analyteKey, specimenKbId, lookupKey, displaySuffix) in normalized)
        {
            var lookup = lookups[(lookupKey, specimenKbId)];
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

            // Каскад, последний шаг (RefSource.Inferred, план "нормы из знаний модели") — ни бланк,
            // ни фиксированный, ни расчётный диапазон KB не дали ответа; если модель САМА
            // предположила ожидаемую норму (dto.RefExpected — заполняется только когда решила, что
            // референса в бланке нет вовсе), используем её как наименее надёжный источник. Проверяем
            // именно после попытки KbCalculated выше — тот надёжнее догадки модели и должен успеть
            // первым. Условие — Flag.Unknown, не RefSource.None: печатный числовой референс,
            // распознанный шагом 1 (например "2-10"), но не сравнимый с качественным значением
            // ("не обнаружено" не парсится как число), тоже даёт Unknown, только с RefSource.Blank —
            // такую запись раньше не пересматривали НИКОГДА (Blank считается терминальным для
            // УСПЕШНОГО определения, но это не он — числа для сравнения не было). TryApplyInferred
            // здесь чаще всего вернёт null (RefExpected пуст, раз RefText был непустым), но пробуем
            // на случай пограничных данных — дёшево, ничего не теряем.
            if (flag == IndicatorFlag.Unknown)
            {
                var inferred = IndicatorFlagCalculator.TryApplyInferred(dto);
                if (inferred is not null)
                {
                    flag = inferred.Value.Flag;
                    refSource = RefSource.Inferred;
                    effLow = inferred.Value.Low;
                    effHigh = inferred.Value.High;
                }
            }

            // Каскад, самый дорогой и самый редкий резервный шаг (RefSource.Inferred,
            // QualitativeNormJudge) — TryApplyInferred выше умеет только числовой диапазон и
            // бинарную полярность "обнаружено/не обнаружено"; описательные качественные результаты
            // (шкалы обильности "+"/"++"/"+++", развёрнутые находки мазка вроде "коккобацилярная,
            // обильно") ни тем, ни другим не раскладываются, а печатный/KB-диапазон, начинающийся
            // не с нуля (например "2-10"), может ошибочно читаться как "отсутствие ⇒ ниже нормы" —
            // короткий прицельный вызов LLM с уже известным контекстом: границы диапазона (что уже
            // определил Calculate ИЛИ, если он вообще ничего не нашёл, общий диапазон из KB), что
            // означают повышенный/пониженный результат ИМЕННО для этого показателя (HighMeans/
            // LowMeans статьи справочника — раздельно, не одной строкой), и dto.RefExpected —
            // собственная более ранняя догадка модели. Отдельный тумблер ("qualitative-judge", не
            // "patient-reference") — принципиально другой по цене и природе вызов, админ может
            // выключить его отдельно.
            if (flag == IndicatorFlag.Unknown &&
                await pipelineConfig.IsEnabledAsync(PipelineCatalog.AnalysisExtraction, "qualitative-judge", ct))
            {
                var kbNorm = kbRow is not null ? LabAnalyteKbPayload.ParseNormExplanations(kbRow.Value.PayloadJson) : null;
                var isNormal = await qualitativeJudge.JudgeAsync(
                    dto.Name, dto.Value, dto.Unit, dto.RefExpected,
                    effLow ?? kbFallback?.Low, effHigh ?? kbFallback?.High, kbNorm, ct);
                if (isNormal is not null)
                {
                    flag = isNormal.Value ? IndicatorFlag.Normal : IndicatorFlag.High;
                    refSource = RefSource.Inferred;
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
                    FamilyDependentId = familyDependentId,
                    TargetUserId = targetUserId,
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

            // Разведённый ключ (AnalyteKeyDisambiguator, §4 плана) — DisplayName из KB совпал бы у
            // всех коллизирующих файлов дословно, суффикс здесь единственное, что отличает их для
            // пользователя. RawDisplayName принудительно непустой в этом случае — иначе подсказка
            // пропала бы именно тогда, когда она нужнее всего.
            if (displaySuffix is not null)
            {
                entity.DisplayName += displaySuffix;
                entity.RawDisplayName = dto.Name + displaySuffix;
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
            // ПО БАЗОВОМУ ключу (lookupKey), не по разведённому analyteKey — иначе в
            // kb.global_lab_analytes_kb ушло бы суффиксированное имя вида "... файл 2" (§4 плана).
            // Дедуп на уровне БД + жёсткий гейт на нерезолвленный источник — оба внутри
            // LabAnalyteEnrichmentRequestService.RequestAsync (единственная точка входа).
            if (lookup.Kind != Kb.KbLookupKind.Hit)
                await enrichmentRequest.RequestAsync(
                    lookupKey, specimenKbId, entity.DisplayName, null, ownerUserId,
                    origin: EnrichmentRequestOrigin.Extraction, ct: ct);
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
        await publisher.PublishAsync(
            new MedicalDocumentExtractedEvent(job.Id, recordId, ownerUserId, IsDoctorVisit: false, allIndicators.Count, deviationCount), ct);
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
            await FailAsync(job, reason, readAttachmentIds, ct, record);
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
        await publisher.PublishAsync(
            new MedicalDocumentExtractedEvent(job.Id, record.Id, record.OwnerUserId, IsDoctorVisit: true, 0, 0), ct);
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

    /// <summary>
    /// Terminal-без-ретрая исход внутри try (в отличие от катастрофы в catch блоке RunAsync —
    /// LM Studio недоступен и т.п., где ретрай ещё имеет смысл). record — null только когда сама
    /// запись уже удалена (единственный вызов без record, см. RunAsync выше) — тогда некому
    /// уведомлять, PublishFailureAsync ниже не вызывается вовсе.
    /// </summary>
    private async Task FailAsync(
        Domain.Entities.MedicalDocumentExtractionJob job, string reason,
        List<Guid> readAttachmentIds, CancellationToken ct, Domain.Entities.MedicalRecord? record = null)
    {
        job.Status = EnrichmentJobStatus.Failed;
        job.Error = reason;
        job.CompletedAt = DateTime.UtcNow;
        // IsTransientFailure остаётся false (дефолт) — все вызовы FailAsync это штатные "не
        // получилось разобрать документ", а не техническая недоступность LM Studio (та всегда
        // идёт через исключение → catch в RunAsync, не через FailAsync).

        // Всегда терминальный исход (нет запланированного ретрая) — запись должна сразу отражать
        // отказ, а не молча оставаться на "None"/"Pending", как будто распознавание ещё не
        // запускалось или всё ещё идёт (см. класс-doc ExtractionStatus enum: раньше в это поле
        // никогда не писался ни Pending, ни Failed — фронт был не в состоянии показать
        // живой/провальный статус после ухода со страницы или F5). No-op, если запись уже удалена.
        await db.MedicalRecords.Where(r => r.Id == job.MedicalRecordId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ExtractionStatus, ExtractionStatus.Failed), ct);

        if (record is not null)
            await PublishFailureAsync(job, record.OwnerUserId, record.Kind == MedicalRecordKind.DoctorVisit, reason, ct);

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

    /// <summary>Уведомляем только о настоящем терминальном отказе — техническую недоступность LM
    /// Studio LmStudioRecoverySweepJob резюмирует молча в течение 7 дней (см. IsTransientFailure на
    /// job), сообщать "не удалось" в этот момент было бы дезинформацией. Общая точка для всех
    /// terminal-путей (три вызова из FailAsync + сам catch в RunAsync) — та же причина, что у
    /// MedicationEnrichmentProcessor.PublishFailureAsync.</summary>
    private async Task PublishFailureAsync(
        Domain.Entities.MedicalDocumentExtractionJob job, Guid ownerUserId, bool isDoctorVisit, string reason, CancellationToken ct)
    {
        if (job.IsTransientFailure || ownerUserId == Guid.Empty) return;
        await publisher.PublishAsync(
            new MedicalDocumentExtractionFailedEvent(job.Id, job.MedicalRecordId, ownerUserId, isDoctorVisit, reason), ct);
    }

    private static string? TryFormatNumeric(string value)
    {
        var normalized = value.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d.ToString(CultureInfo.InvariantCulture)
            : null;
    }
}
