using System.Globalization;
using FamilyHub.Domain.Enums;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Сравнивает распознанный показатель с референсным диапазоном (ветка medicalrecords, задача
/// 5.2). Референс из самого бланка (<see cref="ExtractedLabIndicator.RefLow"/>/RefHigh/RefText)
/// приоритетнее справочника — лаборатория печатает диапазон, актуальный именно для её методики
/// анализа, справочник даёт лишь общий ориентир. Пол пациента нигде в домене не хранится (ни у
/// User, ни у FamilyDependent) — заводить его ради одной этой фичи означало бы отдельную миграцию
/// профиля и согласия на новую категорию ПДн, вне объёма этой ветки: справочник даёт только
/// возрастные диапазоны, без разбиения по полу.
/// </summary>
public static class IndicatorFlagCalculator
{
    /// <summary>Значение внутри диапазона, но ближе чем на 5% к границе к КРИТИЧЕСКОМУ выходу —
    /// пока не считаем: различение Critical пока не подтверждено бланком/справочником, только
    /// Low/Normal/High. Оставлено на будущее расширение (например, явный "критический" диапазон
    /// в справочнике) — сейчас Critical не выставляется никогда.</summary>
    public static IndicatorFlag Calculate(ExtractedLabIndicator indicator, KbReferenceRange? kbFallback, int? ageYears)
    {
        var numericValue = ParseNumeric(indicator.Value);

        var (refLow, refHigh) = (indicator.RefLow, indicator.RefHigh);
        if (refLow is null && refHigh is null && !string.IsNullOrWhiteSpace(indicator.RefText))
        {
            // Качественный референс ("отрицательно", "не обнаружено") — сравниваем текстом, не числом.
            return string.Equals(indicator.Value.Trim(), indicator.RefText.Trim(), StringComparison.OrdinalIgnoreCase)
                ? IndicatorFlag.Normal
                : IndicatorFlag.Unknown;
        }

        if (refLow is null && refHigh is null && kbFallback is not null && MatchesAge(kbFallback, ageYears))
        {
            refLow = kbFallback.Low;
            refHigh = kbFallback.High;
        }

        if (numericValue is null || (refLow is null && refHigh is null)) return IndicatorFlag.Unknown;

        if (refLow is not null && numericValue < refLow) return IndicatorFlag.Low;
        if (refHigh is not null && numericValue > refHigh) return IndicatorFlag.High;
        return IndicatorFlag.Normal;
    }

    private static bool MatchesAge(KbReferenceRange range, int? ageYears)
    {
        if (range.AgeFrom is null && range.AgeTo is null) return true;
        if (ageYears is null) return false;
        return (range.AgeFrom is null || ageYears >= range.AgeFrom) && (range.AgeTo is null || ageYears <= range.AgeTo);
    }

    /// <summary>"118", "5.6", "5,6" (русская десятичная запятая из бланков) → double. Не число
    /// ("отрицательно", "1-3 в п/зр") → null, флаг для такого значения считается по RefText выше.</summary>
    private static double? ParseNumeric(string value)
    {
        var normalized = value.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }
}

/// <summary>Один диапазон из GlobalLabAnalyteKb.PayloadJson.refRanges — используется только когда
/// бланк не напечатал собственный референс (см. Calculate).</summary>
public record KbReferenceRange(int? AgeFrom, int? AgeTo, double? Low, double? High, string? Unit);
