using FamilyHub.Domain.Enums;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>Вложение, переданное на распознавание — расшифрованный байты + заявленный ContentType
/// (нужен диспетчеру FamilyHub.Infrastructure.Documents.IDocumentTextExtractor, чтобы выбрать
/// текстовый путь или vision-OCR) + имя файла (только для диагностики/логов).</summary>
public record DocumentSource(byte[] Content, string ContentType, string FileName);

/// <summary>
/// Абстракция конвейера извлечения показателей анализов и заключений врачей (ветка
/// medicalrecords, задачи 5.2/5.3). По образцу IMedicationSearchProvider (этап 4): реализация
/// подключается конфигом (Extraction:Enabled), не кодом; по умолчанию — Null-реализация, наружу
/// не уходит ничего.
/// </summary>
public interface IMedicalDocumentExtractor
{
    Task<ExtractionResult> ExtractAsync(DocumentSource source, MedicalRecordKind kind, CancellationToken ct = default);
}
