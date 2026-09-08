namespace FamilyHub.Domain.Enums;

/// <summary>Откуда пришёл запрос на обогащение справочника показателей (LabAnalyteEnrichmentJob) —
/// определяет, какой набор гейтов проходит задача в LabAnalyteEnrichmentProcessor. Extraction —
/// текст уже прошёл гейтованное LLM-извлечение документа (LmStudioMedicalDocumentExtractor) и
/// опциональную OCR-коррекцию, второй слой смысловой проверки ему не нужен. ManualEntry — сырой
/// текст, введённый пользователем напрямую (ExtractionQueryService.CreateIndicatorAsync/
/// UpdateIndicatorAsync), никем не проверен — проходит дополнительный гейт правдоподобности
/// (AnalytePlausibilityGuardService). SystemMaintenance — переобогащение уже существующей записи
/// (LabAnalyteKbReenrichJob/LabAnalyteKbRebuildJob), содержимое уже когда-то прошло один из двух
/// гейтов выше при первичной записи.</summary>
public enum EnrichmentRequestOrigin
{
    Extraction = 0,
    ManualEntry = 1,
    SystemMaintenance = 2,
}
