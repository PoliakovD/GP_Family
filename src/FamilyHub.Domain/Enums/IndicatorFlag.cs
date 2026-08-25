namespace FamilyHub.Domain.Enums;

/// <summary>
/// Итог сравнения показателя анализа с референсным диапазоном (ветка medicalrecords, задача
/// 5.2). Unknown — референс не найден ни в бланке, ни в справочнике (kb.global_lab_analytes_kb) —
/// это не то же самое, что Normal: отсутствие данных не должно рисоваться зелёным.
/// </summary>
public enum IndicatorFlag
{
    Unknown = 0,
    Low = 1,
    Normal = 2,
    High = 3,
    Critical = 4,
}
