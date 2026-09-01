namespace FamilyHub.Domain.Enums;

/// <summary>
/// Тип нормы одного референсного диапазона (ветка medicalrecords, редизайн enrich-пайплайна) —
/// систематизированный словарь вместо свободного текста, чтобы фронт мог показать бейдж, а
/// IndicatorFlagCalculator — решить, годится ли строка для автоматического сравнения значения.
/// </summary>
public enum LabNormKind
{
    /// <summary>Фиксированный числовой диапазон (низкий-высокий) — подавляющее большинство
    /// показателей (гемоглобин, глюкоза и т.д.).</summary>
    FixedRange = 0,

    /// <summary>Норма не сводится к фиксированному диапазону — считается по словесной методике
    /// (LabAnalyteSummary.CalculationInstructions), например клиренс креатинина.</summary>
    Calculated = 1,

    /// <summary>Качественный результат ("отрицательно"/"не обнаружено") — не участвует в числовом
    /// сравнении IndicatorFlagCalculator.</summary>
    Qualitative = 2,
}
