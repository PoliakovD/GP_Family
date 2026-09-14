namespace FamilyHub.Domain.Enums;

/// <summary>
/// Откуда взят референсный диапазон показателя (ветка medicalrecords, редизайн v2) — каскад
/// приоритетов реализован в IndicatorFlagCalculator/PatientReferenceCalculator, в этом порядке:
/// 1. Blank — напечатан в самом бланке (лаборатория печатает диапазон под свою методику/единицы,
///    высший приоритет, никогда не переопределяется справочником).
/// 2. KbFixed — фиксированный диапазон из kb.global_lab_analytes_kb.PayloadJson.refRanges,
///    подобранный по полу/возрасту пациента.
/// 3. KbCalculated — фиксированного диапазона нет, но у показателя в KB есть
///    CalculationInstructions — локальная LLM посчитала low/high под конкретного пациента
///    (PatientReferenceCalculator). Фронт показывает бэйдж "рассчитано ИИ" только для этого случая.
/// 4. Inferred — ни бланк, ни справочник ничего не дали, но модель САМА предположила ожидаемую
///    норму по общемедицинским знаниям (ExtractedLabIndicator.RefExpected, заполняется моделью
///    только когда в бланке референса нет вовсе — типичный случай ИППП/качественных панелей).
///    Наименее надёжный источник каскада — MedicalDocumentExtractionProcessor использует его
///    только когда KbFixed/KbCalculated не сработали, и RecalculateIndicatorFlagsJob уступает ему
///    приоритет, как только справочник наполнится. Фронт показывает бэйдж "норма от ИИ".
/// 5. None — промах KB и модели целиком — Flag=Unknown, показатель ждёт RecalculateIndicatorFlagsJob
///    после того, как LabAnalyteEnrichmentProcessor наполнит справочник.
/// </summary>
public enum RefSource
{
    None = 0,
    Blank = 1,
    KbFixed = 2,
    KbCalculated = 3,
    Inferred = 4,
}
