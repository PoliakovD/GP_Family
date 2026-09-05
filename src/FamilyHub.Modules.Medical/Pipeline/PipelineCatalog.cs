namespace FamilyHub.Modules.Medical.Pipeline;

/// <summary>Один шаг одного пайплайна — объявляется В КОДЕ (управление enrich-пайплайном из
/// админки, §2 плана), не в БД: состав и порядок шагов определяются реальной структурой
/// процессора (MedicalDocumentExtractionProcessor и т.п.), БД знает только "включён ли". IsMandatory —
/// обязательные шаги (структурирование самих показателей, суммаризация справочника) нельзя
/// выключить ни из какого UI — без них конвейер бессмыслен, PipelineConfigService.IsEnabledAsync
/// для них даже не спрашивает БД. PromptKey — ключ в реестре промптов (PromptProvider), null у
/// шагов без собственного LLM-вызова.</summary>
public record PipelineStepDeclaration(
    string PipelineKey, string StepKey, string Description, bool IsMandatory, string? PromptKey);

/// <summary>
/// Реестр шагов всех enrich-конвейеров этого модуля — единственный источник истины о том, какие
/// шаги вообще существуют (управление enrich-пайплайном из админки, §2 плана). Порядок в списке —
/// информационный, для отображения в админке: реальная последовательность вызовов зашита в
/// соответствующем процессоре (данные внутри одного прогона имеют жёсткие зависимости — например,
/// коррекция OCR обязана случиться ДО поиска в справочнике, справочник обязан быть найден ДО
/// расчёта персонального референса), поэтому шаги в админке можно включать/выключать, но не
/// переставлять местами: реальный порядок — не данные, а код.
/// </summary>
public static class PipelineCatalog
{
    public const string AnalysisExtraction = "analysis-extraction";
    public const string VisitExtraction = "visit-extraction";
    public const string LabAnalyteEnrichment = "lab-analyte-enrichment";
    public const string MedicationEnrichment = "medication-enrichment";

    /// <summary>Ключ первого, обязательного шага КАЖДОГО пайплайна ниже — проверка легитимности
    /// и prompt injection (LegitimacyGuardService), ДО любого другого вызова LLM или внешнего
    /// поиска этим прогоном. Не по одному объявлению на пайплайн вручную ниже — добавляется через
    /// AllPipelineKeys, чтобы новый пайплайн не мог случайно забыть про него.</summary>
    public const string LegitimacyCheckStep = "legitimacy-check";

    private static readonly IReadOnlyList<string> AllPipelineKeys =
        [AnalysisExtraction, VisitExtraction, LabAnalyteEnrichment, MedicationEnrichment];

    public static readonly IReadOnlyList<PipelineStepDeclaration> Steps =
    [
        .. AllPipelineKeys.Select(key => new PipelineStepDeclaration(
            key, LegitimacyCheckStep, "Проверка легитимности и защиты от prompt injection", true, "guard.legitimacy-check")),

        new(AnalysisExtraction, "extract", "Структурирование показателей из текста/фото бланка", true, "analysis.extract"),
        new(AnalysisExtraction, "specimen-resolve", "Резолвинг источника показателя (биоматериал/исследование)", false, "analysis.specimen-resolve"),
        new(AnalysisExtraction, "ocr-correct", "Коррекция OCR-артефактов в названиях показателей", false, "analysis.ocr-correct"),
        new(AnalysisExtraction, "patient-reference", "Расчёт персонального референса по методике из справочника", false, "analysis.patient-reference"),
        new(AnalysisExtraction, "record-summary", "Суммаризация показателей записи для пользователя", false, "analysis.record-summary"),

        new(VisitExtraction, "extract", "Структурирование заключения врача из текста/фото документа", true, "visit.extract"),

        new(LabAnalyteEnrichment, "summarize", "Суммаризация веб-сниппетов в статью справочника показателей", true, "lab-analyte.summarize"),

        new(MedicationEnrichment, "summarize", "Суммаризация веб-сниппетов в карточку препарата", true, "medication.summarize"),
        new(MedicationEnrichment, "ocr", "Распознавание медикамента по фото упаковки", true, "medication.ocr"),
    ];

    public static PipelineStepDeclaration? Find(string pipelineKey, string stepKey) =>
        Steps.FirstOrDefault(s => s.PipelineKey == pipelineKey && s.StepKey == stepKey);
}
