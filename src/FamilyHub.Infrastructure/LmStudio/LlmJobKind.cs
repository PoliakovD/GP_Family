namespace FamilyHub.Infrastructure.LmStudio;

/// <summary>Какая из четырёх таблиц фоновых задач сейчас вызывает LM Studio — см.
/// LmStudioThinkingContext/LlmThinkingReportService (план "живой поток мыслей"). Соответствие
/// таблицам: Extraction → MedicalDocumentExtractionJob, LabAnalyteEnrichment →
/// LabAnalyteEnrichmentJob, MedicationEnrichment → MedicationEnrichmentJob,
/// VisitMedicationEnrichment → VisitMedicationEnrichmentJob.</summary>
public enum LlmJobKind
{
    Extraction,
    LabAnalyteEnrichment,
    MedicationEnrichment,
    VisitMedicationEnrichment,
}
