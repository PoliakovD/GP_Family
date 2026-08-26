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
/// 4. None — промах KB целиком (или методика есть, но модель не смогла посчитать) — Flag=Unknown,
///    показатель ждёт RecalculateIndicatorFlagsJob после того, как LabAnalyteEnrichmentProcessor
///    наполнит справочник.
/// </summary>
public enum RefSource
{
    None = 0,
    Blank = 1,
    KbFixed = 2,
    KbCalculated = 3,
}
