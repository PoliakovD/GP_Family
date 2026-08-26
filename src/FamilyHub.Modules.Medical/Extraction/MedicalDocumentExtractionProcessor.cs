using System.Globalization;
using System.Text.Json;
using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Messaging;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Attachments;
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
/// разных файлов МЕРЖАТСЯ по ключу (AnalyteKey, Specimen) в существующий набор записи (upsert, не
/// blanket-delete) — повторный клик «Распознать» после добавления нового файла не стирает
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
[AutomaticRetry(Attempts = 3, DelaysInSeconds = [60, 600, 3600])]
public class MedicalDocumentExtractionProcessor(
    AppDbContext db,
    AttachmentService attachments,
    IMedicalDocumentExtractor extractor,
    LabAnalyteKbLookupService kbLookup,
    LabAnalyteEnrichmentRequestService enrichmentRequest,
    PatientReferenceCalculator referenceCalculator,
    LabSummarizer summarizer,
    IDomainEventPublisher publisher,
    ILogger<MedicalDocumentExtractionProcessor> logger)
{
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
                await FailAsync(job, "Мед-запись не найдена (возможно, удалена).", ct);
                return;
            }

            var pending = await db.FileAttachments.AsNoTracking()
                .Where(a => a.OwnerType == Domain.Enums.FileOwnerType.MedicalRecord && a.OwnerId == record.Id && a.ExtractedAt == null)
                .OrderBy(a => a.UploadedAt)
                .ToListAsync(ct);

            if (pending.Count == 0)
            {
                await FailAsync(job, "Нет новых вложений для распознавания — все уже распознаны.", ct);
                return;
            }

            job.TotalFiles = pending.Count;
            await db.SaveChangesAsync(ct);

            var results = new List<ExtractionResult>();
            var fileErrors = new List<string>();

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
                // доходит сюда, и файл останется в очереди на повтор.
                await db.FileAttachments.Where(a => a.Id == attachment.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.ExtractedAt, DateTime.UtcNow), ct);
                job.ProcessedFiles++;
                await db.SaveChangesAsync(ct);
            }

            job.Stage = ExtractionStage.Structuring;
            await db.SaveChangesAsync(ct);

            if (record.Kind == MedicalRecordKind.Analysis)
                await ProcessAnalysisAsync(job, record, results, fileErrors, ct);
            else
                await ProcessVisitAsync(job, record, results, fileErrors, ct);
        }
        catch (Exception ex)
        {
            job.Error = ex.Message;
            await db.SaveChangesAsync(ct);
            logger.LogError(ex, "MedicalDocumentExtractionJob {JobId} упал на попытке {Attempts} — Hangfire повторит.", job.Id, job.Attempts);
            throw;
        }
    }

    private async Task ProcessAnalysisAsync(
        Domain.Entities.MedicalDocumentExtractionJob job, Domain.Entities.MedicalRecord record,
        List<ExtractionResult> results, List<string> fileErrors, CancellationToken ct)
    {
        DateOnly? documentDate = null;
        string? suggestedTitle = null;
        var rawIndicators = new List<(ExtractedLabIndicator Dto, SpecimenType Specimen)>();

        foreach (var result in results)
        {
            if (result.DocumentDate is not null) documentDate = result.DocumentDate;
            if (suggestedTitle is null && !string.IsNullOrWhiteSpace(result.SuggestedTitle)) suggestedTitle = result.SuggestedTitle;
            if (result.LabIndicators is null) continue;

            var specimen = result.Specimen ?? SpecimenType.Unknown;
            rawIndicators.AddRange(result.LabIndicators.Select(dto => (dto, specimen)));
        }

        if (rawIndicators.Count == 0)
        {
            var reason = fileErrors.Count > 0 ? string.Join("; ", fileErrors) : "Не удалось распознать ни одного показателя.";
            await FailAsync(job, reason, ct);
            return;
        }

        job.Stage = ExtractionStage.Linking;
        await db.SaveChangesAsync(ct);

        // Дата документа, если распозналась в бланке, — переопределяет дефолт "сегодня"
        // (проставленный при создании записи). Короткое название — только если ещё не задано
        // (не затираем то, что пользователь мог ввести вручную).
        if (documentDate is not null) record.RecordDate = documentDate.Value;
        if (record.Title is null && suggestedTitle is not null) record.Title = suggestedTitle;

        var recordId = record.Id;
        var ownerUserId = record.OwnerUserId;
        var recordDate = record.RecordDate;

        var normalized = rawIndicators
            .Select(x => (x.Dto, AnalyteKey: LabAnalyteNormalizer.Normalize(x.Dto.Name), x.Specimen))
            .Where(x => x.AnalyteKey.Length > 0)
            .ToList();

        // Один Lookup на уникальное имя — один и тот же показатель может повторяться на одном бланке.
        var lookups = new Dictionary<string, Kb.KbLookupResult>();
        foreach (var (_, analyteKey, _) in normalized)
        {
            if (!lookups.ContainsKey(analyteKey))
                lookups[analyteKey] = await kbLookup.LookupAsync(analyteKey, ct);
        }

        var hitIds = lookups.Values.Where(l => l.Kind == Kb.KbLookupKind.Hit).Select(l => l.KbId!.Value).Distinct().ToList();
        Dictionary<Guid, string> kbPayloads = hitIds.Count == 0
            ? []
            : await db.GlobalLabAnalytesKb.AsNoTracking()
                .Where(k => hitIds.Contains(k.Id))
                .Select(k => new { k.Id, k.PayloadJson })
                .ToDictionaryAsync(x => x.Id, x => x.PayloadJson, ct);

        var (ageYears, sex) = await PatientIdentityResolver.ResolveAsync(db, record, ct);

        // Существующие показатели записи (из прошлых прогонов «Распознать» на этой же записи) —
        // upsert по (AnalyteKey, Specimen), НЕ blanket-delete: повторный клик с новым файлом не
        // должен стирать результаты уже распознанных ранее файлов той же записи.
        var existing = await db.LabIndicators.Where(i => i.MedicalRecordId == recordId).ToListAsync(ct);
        var existingByKey = existing.ToDictionary(i => (i.AnalyteKey, i.Specimen));
        var nextPosition = existing.Count == 0 ? 0 : existing.Max(i => i.Position) + 1;

        foreach (var (dto, analyteKey, specimen) in normalized)
        {
            var lookup = lookups[analyteKey];
            var kbAnalyteId = lookup.Kind == Kb.KbLookupKind.Hit ? lookup.KbId : null;
            var kbPayloadJson = kbAnalyteId is not null && kbPayloads.TryGetValue(kbAnalyteId.Value, out var pj) ? pj : null;

            KbReferenceRange? kbFallback = kbPayloadJson is null
                ? null
                : IndicatorFlagCalculator.PickBestRange(LabAnalyteKbPayload.ParseRefRanges(kbPayloadJson), ageYears, sex);

            var (flag, refSource, effLow, effHigh) = IndicatorFlagCalculator.Calculate(dto, kbFallback, ageYears, sex);

            // Каскад шаг 3: KB-запись есть, фиксированный диапазон не подошёл под пациента, но
            // есть словесная методика расчёта — просим локальную LLM посчитать под конкретного
            // пациента (возраст/пол), в единице измерения бланка.
            if (refSource == RefSource.None && kbPayloadJson is not null)
            {
                var instructions = LabAnalyteKbPayload.ParseCalculationInstructions(kbPayloadJson);
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

            var key = (analyteKey, specimen);
            if (!existingByKey.TryGetValue(key, out var entity))
            {
                entity = new DomainLabIndicator
                {
                    Id = Guid.NewGuid(),
                    MedicalRecordId = recordId,
                    OwnerUserId = ownerUserId,
                    AnalyteKey = analyteKey,
                    Specimen = specimen,
                    Position = nextPosition++,
                    CreatedAt = DateTime.UtcNow,
                };
                db.LabIndicators.Add(entity);
                existingByKey[key] = entity;
            }

            entity.RecordDate = recordDate;
            entity.DisplayName = dto.Name;
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
            // Дедуп на уровне БД (см. LabAnalyteEnrichmentRequestService) — повтор того же
            // показателя на этом же бланке или в другом анализе не плодит вторую задачу.
            if (lookup.Kind != Kb.KbLookupKind.Hit)
                await enrichmentRequest.RequestAsync(analyteKey, dto.Name, null, ownerUserId, ct);
        }

        job.Stage = ExtractionStage.Summarizing;
        await db.SaveChangesAsync(ct);

        // ОДИН проход суммаризатора по ПОЛНОМУ смерженному набору показателей записи — не по
        // каждому файлу отдельно, иначе summary не видел бы показатели, распознанные раньше.
        var allIndicators = existingByKey.Values.ToList();
        var summarized = await summarizer.SummarizeAsync(allIndicators, ct);
        record.SummaryJson = summarized.Success && summarized.Summary is not null
            ? JsonSerializer.Serialize(summarized.Summary)
            : null;
        record.ExtractionStatus = ExtractionStatus.Ready;

        var deviationCount = allIndicators.Count(i => i.Flag is IndicatorFlag.Low or IndicatorFlag.High or IndicatorFlag.Critical);
        job.IndicatorCount = allIndicators.Count;
        job.Status = EnrichmentJobStatus.Completed;
        job.Error = fileErrors.Count > 0 ? string.Join("; ", fileErrors) : null;
        job.CompletedAt = DateTime.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await publisher.PublishAsync(new MedicalDocumentExtractedEvent(job.Id, recordId, ownerUserId, allIndicators.Count, deviationCount), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation(
            "MedicalDocumentExtractionJob {JobId}: распознано {Count} показателей ({Deviations} отклонений) из {Files} файлов.",
            job.Id, allIndicators.Count, deviationCount, results.Count);
    }

    private async Task ProcessVisitAsync(
        Domain.Entities.MedicalDocumentExtractionJob job, Domain.Entities.MedicalRecord record,
        List<ExtractionResult> results, List<string> fileErrors, CancellationToken ct)
    {
        var conclusion = results.Select(r => r.Conclusion).FirstOrDefault(c => c is not null);
        if (conclusion is null)
        {
            var reason = fileErrors.Count > 0 ? string.Join("; ", fileErrors) : "Не удалось распознать заключение врача.";
            await FailAsync(job, reason, ct);
            return;
        }

        var documentDate = results.Select(r => r.DocumentDate).FirstOrDefault(d => d is not null);
        var suggestedTitle = results.Select(r => r.SuggestedTitle).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        if (documentDate is not null) record.RecordDate = documentDate.Value;
        if (record.Title is null && suggestedTitle is not null) record.Title = suggestedTitle;

        record.ExtractedDataJson = JsonSerializer.Serialize(conclusion);
        record.ExtractionStatus = ExtractionStatus.Ready;

        job.IndicatorCount = 0;
        job.Status = EnrichmentJobStatus.Completed;
        job.Error = fileErrors.Count > 0 ? string.Join("; ", fileErrors) : null;
        job.CompletedAt = DateTime.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await publisher.PublishAsync(new MedicalDocumentExtractedEvent(job.Id, record.Id, record.OwnerUserId, 0, 0), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation("MedicalDocumentExtractionJob {JobId}: заключение врача распознано.", job.Id);
    }

    private async Task FailAsync(Domain.Entities.MedicalDocumentExtractionJob job, string reason, CancellationToken ct)
    {
        job.Status = EnrichmentJobStatus.Failed;
        job.Error = reason;
        job.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
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
