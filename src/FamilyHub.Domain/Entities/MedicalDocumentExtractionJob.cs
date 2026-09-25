using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Задача конвейера извлечения (ветка medicalrecords, редизайн v2) — зеркало
/// <see cref="MedicationEnrichmentJob"/>, тот же паттерн Hangfire-очереди с наблюдаемым статусом.
/// Живёт в схеме medical (персональный контекст — какая запись, кто попросил).
///
/// v2: задача теперь на ЗАПИСЬ целиком, не на одно вложение — «Распознать» обрабатывает все ещё
/// не распознанные файлы записи (FileAttachment.ExtractedAt=null) последовательно за один прогон
/// (см. MedicalDocumentExtractionProcessor), не по клику на каждый файл отдельно. Частичный
/// уникальный индекс по MedicalRecordId среди Pending/Running (см.
/// MedicalDocumentExtractionJobConfiguration) — дедуп повторного клика «Распознать» на одной записи.
/// </summary>
public class MedicalDocumentExtractionJob : IPipelineJob
{
    public Guid Id { get; set; }

    public Guid MedicalRecordId { get; set; }

    public Guid RequestedByUserId { get; set; }

    public EnrichmentJobStatus Status { get; set; } = EnrichmentJobStatus.Pending;

    public ExtractionStage Stage { get; set; } = ExtractionStage.Queued;

    public int Attempts { get; set; }

    /// <summary>Сколько показателей сохранено — только для отображения прогресса, не источник
    /// истины (сами показатели — в LabIndicators).</summary>
    public int IndicatorCount { get; set; }

    /// <summary>Сколько вложений обрабатывается в этом прогоне (FileAttachment.ExtractedAt=null
    /// на момент старта) — для прогресса «файл 2 из 3» на фронте.</summary>
    public int TotalFiles { get; set; }

    /// <summary>Сколько уже обработано (успешно или нет) — растёт по одному после каждого файла.</summary>
    public int ProcessedFiles { get; set; }

    public string? Error { get; set; }

    /// <summary>См. LabAnalyteEnrichmentJob.FailureReason — та же машиночитаемая классификация.
    /// В этом конвейере сегодня заполняется только для терминального LmStudioUnavailable/Unknown
    /// (см. MedicalDocumentExtractionProcessor) — извлечение не проходит через доверенные домены.</summary>
    public EnrichmentFailureReason? FailureReason { get; set; }

    /// <summary>true — последний отказ был техническим (LM Studio недоступен), не смысловым.
    /// Проставляется только на терминальном Failed после исчерпания [AutomaticRetry]
    /// (см. MedicalDocumentExtractionProcessor) — LmStudioRecoverySweepJob находит такие задачи и
    /// возвращает их в очередь, когда сервер снова станет доступен.</summary>
    public bool IsTransientFailure { get; set; }

    /// <summary>true — задача ЖДЁТ ИИ: LM Studio недоступен (при постановке или посреди прогона),
    /// задача остаётся Pending и не сгорает по [AutomaticRetry]; LmStudioRecoverySweepJob запускает её
    /// (сбрасывая флаг), как только сервер снова отвечает. Фронт показывает «ждём ИИ» вместо ошибки.</summary>
    public bool WaitingForAi { get; set; }

    /// <summary>Живой обрывок текста внутри ещё не закрытого &lt;think&gt; модели, пока задача
    /// реально думает (план "живой поток мыслей") — null, когда модель сейчас не думает вслух
    /// (между вызовами, security-гейты, &lt;think&gt; уже закрылся) или задача не Running.
    /// Пишется LlmThinkingReportService throttled-обновлениями напрямую (ExecuteUpdateAsync), не
    /// через SaveChangesAsync этого job — см. LmStudioThinkingContext/FamilyHub.Infrastructure.LmStudio.</summary>
    public string? CurrentThought { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}
