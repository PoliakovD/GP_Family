using System.Globalization;
using FamilyHub.Domain.Enums;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Сравнивает распознанный показатель с референсным диапазоном (ветка medicalrecords, редизайн
/// v2) — каскад приоритетов (см. FamilyHub.Domain.Enums.RefSource):
/// 1. Референс из самого бланка — лаборатория печатает диапазон под свою методику/единицы.
/// 2. Фиксированный диапазон из GlobalLabAnalyteKb, подобранный по полу (identity rework:
///    User.Gender/FamilyDependent.Gender) и возрасту пациента.
/// Диапазон, посчитанный локальной LLM по методике из KB (RefSource.KbCalculated) — ТРЕТИЙ шаг
/// каскада, но он не умещается в этот чистый компаратор (требует внешнего вызова) — см.
/// PatientReferenceCalculator и ApplyCalculatedRange ниже, вызывается процессором отдельно, когда
/// этот метод вернул RefSource.None, а у KB-записи есть CalculationInstructions.
/// </summary>
public static class IndicatorFlagCalculator
{
    /// <summary>Значение внутри диапазона, но ближе чем на 5% к границе к КРИТИЧЕСКОМУ выходу —
    /// пока не считаем: различение Critical пока не подтверждено бланком/справочником, только
    /// Low/Normal/High. Оставлено на будущее расширение (например, явный "критический" диапазон
    /// в справочнике) — сейчас Critical не выставляется никогда.</summary>
    public static (IndicatorFlag Flag, RefSource Source, double? EffectiveLow, double? EffectiveHigh) Calculate(
        ExtractedLabIndicator indicator, KbReferenceRange? kbFallback, int? ageYears, Gender? sex)
    {
        var numericValue = ParseNumeric(indicator.Value);

        if (indicator.RefLow is not null || indicator.RefHigh is not null)
        {
            return (CompareToRange(numericValue, indicator.RefLow, indicator.RefHigh), RefSource.Blank, indicator.RefLow, indicator.RefHigh);
        }

        if (!string.IsNullOrWhiteSpace(indicator.RefText))
        {
            // Качественный референс ("отрицательно", "не обнаружено") — сравниваем текстом, не числом.
            var flag = string.Equals(indicator.Value.Trim(), indicator.RefText.Trim(), StringComparison.OrdinalIgnoreCase)
                ? IndicatorFlag.Normal
                : IndicatorFlag.Unknown;
            return (flag, RefSource.Blank, null, null);
        }

        if (kbFallback is not null && MatchesPatient(kbFallback, ageYears, sex))
        {
            return (CompareToRange(numericValue, kbFallback.Low, kbFallback.High), RefSource.KbFixed, kbFallback.Low, kbFallback.High);
        }

        return (IndicatorFlag.Unknown, RefSource.None, null, null);
    }

    /// <summary>Применяет уже посчитанный диапазон (PatientReferenceCalculator, RefSource.KbCalculated) —
    /// тот же числовой компаратор, что Calculate, чтобы не дублировать пороговую логику.</summary>
    public static IndicatorFlag ApplyCalculatedRange(string value, double? low, double? high) =>
        CompareToRange(ParseNumeric(value), low, high);

    /// <summary>Диапазон под конкретные пол+возраст, если есть; иначе общий (без ограничений),
    /// иначе первый попавшийся — лучше приблизительный ориентир, чем никакого. Общий для
    /// MedicalDocumentExtractionProcessor (свежее распознавание) и RecalculateIndicatorFlagsJob
    /// (дозаполнение задним числом после того, как справочник наполнился).</summary>
    public static KbReferenceRange? PickBestRange(List<KbReferenceRange> ranges, int? ageYears, Gender? sex)
    {
        if (ranges.Count == 0) return null;

        var bySex = sex is null ? ranges : ranges.Where(r => r.Sex is null || r.Sex == sex).ToList();
        if (bySex.Count == 0) bySex = ranges;

        if (ageYears is not null)
        {
            var ageMatch = bySex.FirstOrDefault(r =>
                (r.AgeFrom is not null || r.AgeTo is not null) &&
                (r.AgeFrom is null || ageYears >= r.AgeFrom) &&
                (r.AgeTo is null || ageYears <= r.AgeTo));
            if (ageMatch is not null) return ageMatch;
        }

        return bySex.FirstOrDefault(r => r.AgeFrom is null && r.AgeTo is null) ?? bySex[0];
    }

    private static IndicatorFlag CompareToRange(double? numericValue, double? refLow, double? refHigh)
    {
        if (numericValue is null || (refLow is null && refHigh is null)) return IndicatorFlag.Unknown;
        if (refLow is not null && numericValue < refLow) return IndicatorFlag.Low;
        if (refHigh is not null && numericValue > refHigh) return IndicatorFlag.High;
        return IndicatorFlag.Normal;
    }

    /// <summary>Диапазон с указанным полом подходит только пациенту того же пола (или неизвестного —
    /// тогда лучше промолчать, чем ошибочно сравнить с чужим полом); диапазон без пола — универсальный.</summary>
    private static bool MatchesPatient(KbReferenceRange range, int? ageYears, Gender? sex)
    {
        if (range.Sex is not null && range.Sex != sex) return false;
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
/// бланк не напечатал собственный референс (см. Calculate). Sex=null — общий диапазон, годится
/// любому полу; Sex задан — годится только пациенту того же пола (см. MatchesPatient).</summary>
public record KbReferenceRange(int? AgeFrom, int? AgeTo, Gender? Sex, double? Low, double? High, string? Unit);
