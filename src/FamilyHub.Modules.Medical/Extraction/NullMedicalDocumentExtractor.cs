using FamilyHub.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Реализация по умолчанию — распознавание не поддерживается ни для одного вида записи. Задачи
/// 5.2/5.3 ещё не реализованы: ни очереди, ни эндпоинта, вызывающего этот интерфейс, пока нет —
/// заготовка только под будущий конвейер (см. IMedicalDocumentExtractor).
/// </summary>
public class NullMedicalDocumentExtractor(ILogger<NullMedicalDocumentExtractor> logger) : IMedicalDocumentExtractor
{
    public Task<ExtractionResult> ExtractAsync(DocumentSource source, MedicalRecordKind kind, CancellationToken ct = default)
    {
        logger.LogDebug("Распознавание документа ({Kind}) запрошено, но конвейер ещё не реализован — пропуск.", kind);
        return Task.FromResult(new ExtractionResult(Supported: false, LabIndicators: null, Conclusion: null));
    }
}
