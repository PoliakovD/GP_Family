namespace FamilyHub.Domain.Enums;

/// <summary>
/// Прогресс задачи <see cref="Entities.MedicalDocumentExtractionJob"/> внутри одного прогона
/// (ветка medicalrecords) — детальнее, чем <see cref="ExtractionStatus"/> на самой
/// <see cref="Entities.MedicalRecord"/>: пользователь видит, где именно завис/остановился
/// конкретный документ, а не просто Pending/Ready/Failed.
/// </summary>
public enum ExtractionStage
{
    Queued = 0,
    Decoding = 1,
    Ocr = 2,
    Structuring = 3,
    Linking = 4,
    Summarizing = 5,
}
