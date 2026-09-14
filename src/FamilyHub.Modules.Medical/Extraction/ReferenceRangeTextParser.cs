using System.Globalization;
using System.Text.RegularExpressions;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Разбирает референсный диапазон, напечатанный в бланке ТЕКСТОМ (не разложенный моделью на
/// refLow/refHigh) — живой баг с бланков: "&lt;47" должно означать диапазон 0–47, "&gt;47" — "от 47
/// и без верхней границы", а не оставаться нераспознанным текстом, из-за которого показатель
/// навсегда застревает на IndicatorFlag.Unknown (см. IndicatorFlagCalculator.Calculate, класс-док
/// каскада). Ноль как нижняя граница одностороннего "меньше" — осознанное упрощение (лабораторные
/// величины неотрицательны), не медицинское суждение о минимуме; строгое/нестрогое неравенство
/// (&lt; vs ≤) не различаем — тоже осознанное упрощение, разница на практике не влияет на Flag.
/// </summary>
public static class ReferenceRangeTextParser
{
    private static readonly Regex TwoSided = new(
        @"^(-?\d+(?:[.,]\d+)?)\s*(?:-|–|—|\.\.)\s*(-?\d+(?:[.,]\d+)?)$", RegexOptions.Compiled);

    // Верхняя граница: "<47", "≤47", "до 47", "менее 47", "не более 47", "47 и менее".
    private static readonly Regex UpperBound = new(
        @"^(?:<|≤|до|не\s*более|менее)\s*(-?\d+(?:[.,]\d+)?)$|^(-?\d+(?:[.,]\d+)?)\s*и\s*менее$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Нижняя граница: ">47", "≥47", "от 47", "более 47", "не менее 47", "47 и более".
    private static readonly Regex LowerBound = new(
        @"^(?:>|≥|от|не\s*менее|более)\s*(-?\d+(?:[.,]\d+)?)$|^(-?\d+(?:[.,]\d+)?)\s*и\s*более$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Числовая часть censored-значения самого показателя ("<0,5", ">1000") — не диапазон, а точка
    // сравнения; ParseCensoredValue ниже.
    private static readonly Regex CensoredValue = new(
        @"^(?:<|≤|>|≥)\s*(-?\d+(?:[.,]\d+)?)$", RegexOptions.Compiled);

    /// <summary>"130-160" → (130, 160); "&lt;47"/"до 47"/"не более 47" → (0, 47); "&gt;47"/"от 47" →
    /// (47, null); всё остальное ("отрицательно", "1-3 в п/зр", пусто) → null — не диапазон, дальше
    /// по каскаду (см. Calculate).</summary>
    public static (double? Low, double? High)? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();

        var two = TwoSided.Match(s);
        if (two.Success)
        {
            var low = ParseNumber(two.Groups[1].Value);
            var high = ParseNumber(two.Groups[2].Value);
            if (low is not null && high is not null) return (low, high);
        }

        var upper = UpperBound.Match(s);
        if (upper.Success)
        {
            var num = ParseNumber(upper.Groups[1].Success ? upper.Groups[1].Value : upper.Groups[2].Value);
            if (num is not null) return (0, num);
        }

        var lower = LowerBound.Match(s);
        if (lower.Success)
        {
            var num = ParseNumber(lower.Groups[1].Success ? lower.Groups[1].Value : lower.Groups[2].Value);
            if (num is not null) return (num, null);
        }

        return null;
    }

    /// <summary>Значение напечатано как censored-число ("&lt;0,5", "&gt;1000" — за пределами
    /// чувствительности метода) — числовая часть используется как точка сравнения с диапазоном
    /// (тот же приём, что у лабораторного софта); неоднозначность на самой границе диапазона (когда
    /// "&lt;0,5" сравнивается с "0,2–1,0") — осознанно не разрешаем, обычный CompareToRange отработает
    /// по числу как есть.</summary>
    public static double? ParseCensoredValue(string value)
    {
        var match = CensoredValue.Match(value.Trim());
        return match.Success ? ParseNumber(match.Groups[1].Value) : null;
    }

    internal static double? ParseNumber(string value) =>
        double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
}
