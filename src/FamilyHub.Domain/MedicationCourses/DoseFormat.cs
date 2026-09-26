using System.Globalization;
using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.MedicationCourses;

/// <summary>Подписи дозы для мест, где текст строит сервер (запись дневника, документы). В интерфейсе
/// тот же формат собирает клиент, но записи дневника хранятся строкой — она должна выглядеть как «1 таб.».</summary>
public static class DoseFormat
{
    /// <summary>«1 таб.», «0,5 таб.», «2,5 мл».</summary>
    public static string Units(decimal units, DoseUnit unit)
    {
        var number = Math.Round(units, 2).ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',');
        return $"{number} {Suffix(unit)}";
    }

    public static string Suffix(DoseUnit unit) => unit switch
    {
        DoseUnit.Tablet => "таб.",
        DoseUnit.Capsule => "капс.",
        DoseUnit.Ml => "мл",
        DoseUnit.Drop => "капл.",
        DoseUnit.Sachet => "пак.",
        _ => "доз.",
    };
}
