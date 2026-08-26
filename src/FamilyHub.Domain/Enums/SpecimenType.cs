namespace FamilyHub.Domain.Enums;

/// <summary>
/// Биоматериал, из которого получен показатель (ветка medicalrecords, редизайн v2) — без него
/// "лейкоциты" из общего анализа крови и из общего анализа мочи были бы неотличимы на одном
/// графике/в одном списке "мои показатели". Извлекается LLM на уровне ДОКУМЕНТА (один анализ —
/// обычно один биоматериал), но хранится на каждом <see cref="Entities.LabIndicator"/>, потому что
/// несколько вложений с разным биоматериалом мержатся в одну запись (см.
/// MedicalDocumentExtractionProcessor).
/// </summary>
public enum SpecimenType
{
    Unknown = 0,
    Blood = 1,
    Urine = 2,
    Stool = 3,
    VaginalSwab = 4,
    Saliva = 5,
    Other = 6,
}
