namespace FamilyHub.Infrastructure.Documents;

/// <summary>
/// Диспетчер форматов вложения по <c>ContentType</c> — единственная точка, которая решает,
/// пойдёт документ по дешёвому текстовому пути или по дорогому vision-OCR (см. план ветки
/// medicalrecords: «текст напрямую, vision — только для сканов»). Не знает ничего про домен
/// (анализы/выписки, LM Studio) — это забота
/// <c>FamilyHub.Modules.Medical.Extraction.LmStudioMedicalDocumentExtractor</c>, который
/// использует результат этого сервиса как вход.
/// </summary>
public interface IDocumentTextExtractor
{
    Task<DocumentContent> ExtractAsync(byte[] bytes, string contentType, CancellationToken ct = default);
}
