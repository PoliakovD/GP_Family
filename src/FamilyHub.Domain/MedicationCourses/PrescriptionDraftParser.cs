using System.Globalization;
using System.Text.RegularExpressions;
using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.MedicationCourses;

/// <summary>Черновик полей формы курса, угаданный из текста назначения. Всё необязательно:
/// то, что не распознано, пользователь заполняет сам («поля заполнены из назначения — проверьте»).</summary>
public record PrescriptionDraft(
    DoseScheduleMode? Mode,
    int? TimesPerDay,
    int? IntervalHours,
    decimal? Units,
    DoseUnit? Unit,
    int? DurationDays,
    FoodRelation? Food);

/// <summary>
/// Разбор свободного текста назначения («по 1 таблетке 2 раза в день после еды, 8 недель»).
/// Назначения в системе — строка DosageInstructions без структуры (см. PrescribedMedication),
/// поэтому это эвристика: лучше не угадать, чем угадать неверно, — сомнительное остаётся null.
/// </summary>
public static partial class PrescriptionDraftParser
{
    [GeneratedRegex(@"(\d+(?:[.,]\d+)?|\d+\s*/\s*\d+)\s*(таб|капс|мл|капл|пакет|саше|доз)")]
    private static partial Regex UnitsRegex();

    [GeneratedRegex(@"(\d+)\s*(?:раз[а]?|р\.?)\s*(?:в|/)\s*(?:день|сутки|сут|д)\b")]
    private static partial Regex TimesNumberRegex();

    [GeneratedRegex(@"\b(один|одна|два|две|три|четыре|дважды|трижды|однократно)\b")]
    private static partial Regex TimesWordRegex();

    [GeneratedRegex(@"кажды[ехй]\s*(\d+)\s*(?:ч\b|час)")]
    private static partial Regex IntervalRegex();

    [GeneratedRegex(@"(\d+)\s*(дн|день|дня|дней|недел|нед\b|месяц|мес\b)")]
    private static partial Regex DurationRegex();

    private static readonly string[] AsNeededMarkers =
        ["при боли", "по необходимости", "при необходимости", "при температуре", "при головной боли", "при кашле", "при приступе"];

    public static PrescriptionDraft Parse(string? dosageInstructions)
    {
        var text = (dosageInstructions ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text)) return new PrescriptionDraft(null, null, null, null, null, null, null);

        var (units, unit) = ParseUnits(text);
        var food = ParseFood(text);
        var duration = ParseDuration(text);

        if (AsNeededMarkers.Any(text.Contains))
            return new PrescriptionDraft(DoseScheduleMode.AsNeeded, null, null, units, unit, duration, food);

        if (IntervalRegex().Match(text) is { Success: true } im
            && int.TryParse(im.Groups[1].Value, out var hours) && DoseSchedule.AllowedIntervalHours.Contains(hours))
            return new PrescriptionDraft(DoseScheduleMode.EveryNHours, null, hours, units, unit, duration, food);

        var times = ParseTimesPerDay(text);
        return new PrescriptionDraft(times is null ? null : DoseScheduleMode.TimesPerDay, times, null, units, unit, duration, food);
    }

    private static (decimal? Units, DoseUnit? Unit) ParseUnits(string text)
    {
        var m = UnitsRegex().Match(text);
        if (!m.Success) return (null, null);

        var raw = m.Groups[1].Value.Replace(" ", "");
        decimal value;
        if (raw.Contains('/'))
        {
            var parts = raw.Split('/');
            if (!decimal.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var n)
                || !decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var d) || d == 0)
                return (null, null);
            value = n / d;
        }
        else if (!decimal.TryParse(raw.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value))
        {
            return (null, null);
        }
        if (value is < MedicationCourseRules.MinUnits or > MedicationCourseRules.MaxUnits) return (null, null);

        var unit = m.Groups[2].Value switch
        {
            "таб" => DoseUnit.Tablet,
            "капс" => DoseUnit.Capsule,
            "мл" => DoseUnit.Ml,
            "капл" => DoseUnit.Drop,
            "пакет" or "саше" => DoseUnit.Sachet,
            _ => DoseUnit.Dose,
        };
        return (Math.Round(value, 2), unit);
    }

    private static int? ParseTimesPerDay(string text)
    {
        if (TimesNumberRegex().Match(text) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n)
            && n is >= 1 and <= MedicationCourseRules.MaxTimesPerDay)
            return n;

        if (text.Contains("утром и вечером")) return 2;
        if (text.Contains("раз в день") || text.Contains("раз в сутки")) return 1;

        if (TimesWordRegex().Match(text) is { Success: true } w)
        {
            return w.Groups[1].Value switch
            {
                "один" or "одна" or "однократно" => 1,
                "два" or "две" or "дважды" => 2,
                "три" or "трижды" => 3,
                "четыре" => 4,
                _ => null,
            };
        }
        return null;
    }

    private static int? ParseDuration(string text)
    {
        if (DurationRegex().Match(text) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) && n > 0)
        {
            var days = m.Groups[2].Value switch
            {
                "дн" or "день" or "дня" or "дней" => n,
                "недел" or "нед" => n * 7,
                _ => n * 30,
            };
            return days <= 3650 ? days : null;
        }
        if (text.Contains("на неделю")) return 7;
        if (text.Contains("на месяц") || text.Contains("в течение месяца")) return 30;
        return null;
    }

    private static FoodRelation? ParseFood(string text)
    {
        if (text.Contains("до еды") || text.Contains("перед едой") || text.Contains("натощак")) return FoodRelation.Before;
        if (text.Contains("во время еды") || text.Contains("с едой") || text.Contains("во время приёма пищи")) return FoodRelation.With;
        if (text.Contains("после еды") || text.Contains("после приёма пищи")) return FoodRelation.After;
        return null;
    }
}
