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
/// 4. Inferred — ни бланк, ни справочник ничего не дали, но модель САМА определила норму. Два
///    механизма: (а) IndicatorFlagCalculator.TryApplyInferred — дешёвый, детерминированный разбор
///    ExtractedLabIndicator.RefExpected (числовой диапазон или полярность "обнаружено/не
///    обнаружено"; RefExpected заполняется моделью только когда в бланке референса нет вовсе —
///    типичный случай ИППП/качественных панелей); (б) QualitativeNormJudge — короткий прицельный
///    LLM-вызов, последний и самый дорогой резервный шаг, когда (а) не смог разобрать текст
///    (шкалы обильности "+"/"++"/"+++", развёрнутые описательные находки мазков). Наименее
///    надёжный источник каскада — MedicalDocumentExtractionProcessor пробует оба только когда
///    KbFixed/KbCalculated не сработали, и RecalculateIndicatorFlagsJob уступает им приоритет, как
///    только справочник наполнится. Фронт показывает бэйдж "норма от ИИ" для обоих механизмов.
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
