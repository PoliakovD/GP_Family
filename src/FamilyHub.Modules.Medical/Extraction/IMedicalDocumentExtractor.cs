using FamilyHub.Domain.Enums;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Абстракция OCR-конвейера бланков анализов и заключений врачей — задачи 5.2/5.3, ПОКА НЕ
/// РЕАЛИЗОВАНЫ (см. .claude/plans/medical-platform/stage/stage-5). Заготовка контракта нужна
/// сейчас, чтобы MedicalRecord.ExtractedDataJson/ExtractionStatus не пришлось вводить отдельной
/// миграцией позже. По образцу IMedicationSearchProvider (этап 4): реализация подключается
/// конфигом, не кодом; по умолчанию — Null-реализация, наружу не уходит ничего.
/// </summary>
public interface IMedicalDocumentExtractor
{
    Task<ExtractionResult> ExtractAsync(Stream scan, MedicalRecordKind kind, CancellationToken ct = default);
}
