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
/// Шаги конвейера извлечения (ветка medicalrecords, задачи 5.2/5.3) — декодирование → OCR/текстовый
/// разбор → структурирование → привязка к справочнику показателей → сохранение → суммаризация →
/// событие. Структура — один в один MedicationEnrichmentProcessor (этап 4): выделенная Hangfire-
/// очередь "extraction" с одним воркером (LM Studio — один ноутбук за WireGuard, параллелить
/// нечего), AutomaticRetry только на настоящие сбои, ожидаемые исходы — Failed без ретрая.
///
/// Известное упрощение этой ветки (см. план): KB-фолбэк референсов (kbFallback в
/// IndicatorFlagCalculator.Calculate) сейчас всегда null — сам конвейер обогащения справочника
/// показателей (аналог MedicationEnrichmentProcessor для kb.global_lab_analytes_kb) в этой ветке
/// не реализован, привязка к KB (KbAnalyteId) только читает то, что уже могло быть туда занесено
/// вручную/сидом. Референс из самого бланка при этом работает полностью — это основной случай.
/// </summary>
[Queue("extraction")]
[AutomaticRetry(Attempts = 3, DelaysInSeconds = [60, 600, 3600])]
public class MedicalDocumentExtractionProcessor(
    AppDbContext db,
    AttachmentService attachments,
    IMedicalDocumentExtractor extractor,
    LabAnalyteKbLookupService kbLookup,
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

            var download = await attachments.GetDownloadAsync(job.AttachmentId, ct);
            if (download is null)
            {
                await FailAsync(job, "Вложение не найдено.", ct);
                return;
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
            {
                await FailAsync(job, result.FailureReason ?? "Формат не поддержан распознаванием.", ct);
                return;
            }

            job.Stage = ExtractionStage.Structuring;
            await db.SaveChangesAsync(ct);

            if (record.Kind == MedicalRecordKind.Analysis)
                await ProcessAnalysisAsync(job, record.Id, record.OwnerUserId, record.RecordDate, result, ct);
            else
                await ProcessVisitAsync(job, record, result, ct);
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
        Domain.Entities.MedicalDocumentExtractionJob job, Guid recordId, Guid ownerUserId, DateOnly recordDate,
        ExtractionResult result, CancellationToken ct)
    {
        if (result.LabIndicators is null || result.LabIndicators.Count == 0)
        {
            await FailAsync(job, result.FailureReason ?? "Не удалось распознать ни одного показателя.", ct);
            return;
        }

        job.Stage = ExtractionStage.Linking;
        await db.SaveChangesAsync(ct);

        // Повторное распознавание того же вложения полностью заменяет ранее сохранённые
        // показатели этой записи — не накапливаем дубликаты между попытками.
        await db.LabIndicators.Where(i => i.MedicalRecordId == recordId).ExecuteDeleteAsync(ct);

        var entities = new List<DomainLabIndicator>();
        var position = 0;
        foreach (var dto in result.LabIndicators)
        {
            var analyteKey = LabAnalyteNormalizer.Normalize(dto.Name);
            if (analyteKey.Length == 0) continue;

            var lookup = await kbLookup.LookupAsync(analyteKey, ct);
            var kbAnalyteId = lookup.Kind == Kb.KbLookupKind.Hit ? lookup.KbId : null;

            var flag = IndicatorFlagCalculator.Calculate(dto, kbFallback: null, ageYears: null);

            entities.Add(new DomainLabIndicator
            {
                Id = Guid.NewGuid(),
                MedicalRecordId = recordId,
                RecordDate = recordDate,
                OwnerUserId = ownerUserId,
                AnalyteKey = analyteKey,
                DisplayName = dto.Name,
                KbAnalyteId = kbAnalyteId,
                Flag = flag,
                Position = position++,
                ValueRaw = dto.Value,
                ValueNumericText = TryFormatNumeric(dto.Value),
                Unit = dto.Unit,
                RefLowText = dto.RefLow?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                RefHighText = dto.RefHigh?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                RefText = dto.RefText,
                CreatedAt = DateTime.UtcNow,
            });
        }

        db.LabIndicators.AddRange(entities);
        job.Stage = ExtractionStage.Summarizing;
        await db.SaveChangesAsync(ct);

        var record = await db.MedicalRecords.FirstAsync(r => r.Id == recordId, ct);
        var summarized = await summarizer.SummarizeAsync(entities, ct);
        record.SummaryJson = summarized.Success && summarized.Summary is not null
            ? JsonSerializer.Serialize(summarized.Summary)
            : null;
        record.ExtractionStatus = ExtractionStatus.Ready;

        var deviationCount = entities.Count(i => i.Flag is IndicatorFlag.Low or IndicatorFlag.High or IndicatorFlag.Critical);
        job.IndicatorCount = entities.Count;
        job.Status = EnrichmentJobStatus.Completed;
        job.CompletedAt = DateTime.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await publisher.PublishAsync(new MedicalDocumentExtractedEvent(job.Id, recordId, ownerUserId, entities.Count, deviationCount), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation(
            "MedicalDocumentExtractionJob {JobId}: распознано {Count} показателей ({Deviations} отклонений).",
            job.Id, entities.Count, deviationCount);
    }

    private async Task ProcessVisitAsync(
        Domain.Entities.MedicalDocumentExtractionJob job, Domain.Entities.MedicalRecord record, ExtractionResult result, CancellationToken ct)
    {
        if (result.Conclusion is null)
        {
            await FailAsync(job, result.FailureReason ?? "Не удалось распознать заключение врача.", ct);
            return;
        }

        record.ExtractedDataJson = JsonSerializer.Serialize(result.Conclusion);
        record.ExtractionStatus = ExtractionStatus.Ready;

        job.IndicatorCount = 0;
        job.Status = EnrichmentJobStatus.Completed;
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
        return double.TryParse(normalized, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }
}
