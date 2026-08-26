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
public class MedicalDocumentExtractionJob
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

    public DateTime CreatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}
