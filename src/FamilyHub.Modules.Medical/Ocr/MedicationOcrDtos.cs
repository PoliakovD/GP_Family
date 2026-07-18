namespace FamilyHub.Modules.Medical.Ocr;

/// <summary>
/// Результат оцифровки медикамента по фото. Name/ExpiryDate выделены отдельно — зеркалят
/// единственные две обязательные колонки Medication (см. Medication.cs); Data — всё
/// остальное, что нашла модель (производитель, дозировка, серия и т.д.), одним плоским
/// словарём, без разделения на "известные" и "дополнительные" поля.
/// </summary>
public record MedicationOcrResponse(bool Success, string? Name, string? ExpiryDate, Dictionary<string, string>? Data, string? Error);
