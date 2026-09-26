using System.Globalization;
using System.Text.RegularExpressions;

namespace FamilyHub.Domain.MedicationCourses;

/// <summary>
/// Остаток препарата в аптечке. В Medication количество — строка внутри DataJson («22 таб.»,
/// «1,5 упаковки»), без единицы измерения, поэтому число выделяется регэкспом, а суффикс при
/// списании сохраняется как есть. Не разобралось — списание пропускаем, приём всё равно отмечается.
/// </summary>
public static partial class StockMath
{
    [GeneratedRegex(@"^(\s*)(\d+(?:[.,]\d+)?)(.*)$", RegexOptions.Singleline)]
    private static partial Regex QuantityRegex();

    public static bool TryParse(string? text, out decimal quantity)
    {
        quantity = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = QuantityRegex().Match(text);
        if (!m.Success) return false;
        return decimal.TryParse(m.Groups[2].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out quantity);
    }

    /// <summary>Подставляет новое число в исходную строку, сохраняя пробелы, разделитель и суффикс.
    /// null — исходная строка не начинается с числа.</summary>
    public static string? ReplaceQuantity(string? original, decimal newQuantity)
    {
        if (string.IsNullOrWhiteSpace(original)) return null;
        var m = QuantityRegex().Match(original);
        if (!m.Success) return null;
        if (newQuantity < 0) newQuantity = 0;
        var useComma = m.Groups[2].Value.Contains(',');
        var number = Math.Round(newQuantity, 2).ToString("0.##", CultureInfo.InvariantCulture);
        if (useComma) number = number.Replace('.', ',');
        return m.Groups[1].Value + number + m.Groups[3].Value;
    }

    /// <summary>На сколько полных дней хватит запаса при среднем расходе. null — расход неизвестен (0).</summary>
    public static int? DaysCovered(decimal quantity, decimal averageUnitsPerDay) =>
        averageUnitsPerDay <= 0 ? null : (int)Math.Floor(quantity / averageUnitsPerDay);

    /// <summary>Сколько не хватает до конца курса («купите ещё N»); 0 — хватает.</summary>
    public static decimal Shortfall(decimal needed, decimal inStock) =>
        needed > inStock ? Math.Ceiling(needed - inStock) : 0;
}
