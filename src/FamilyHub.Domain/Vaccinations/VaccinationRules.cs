namespace FamilyHub.Domain.Vaccinations;

/// <summary>Содержимое одной вносимой прививки — то, что проверяют правила. Ровно одно из
/// (SeriesCode+DoseIndex) — доза из каталога, либо CustomName — «прививка не из календаря».</summary>
public record VaccinationContent(
    string? SeriesCode,
    int? DoseIndex,
    string? CustomName,
    string? VaccineName,
    VaccinationKind Kind,
    DateOnly? Date,
    VaccinationDatePrecision? DatePrecision,
    DateOnly? BirthDate,
    DateOnly Today);

/// <summary>Правила ввода прививки: валидация даты и согласованности полей. Единое место для всех клиентов.</summary>
public static class VaccinationRules
{
    public const int MaxCustomNameLength = 200;
    public const int MaxVaccineNameLength = 200;
    public const int MaxReactionNoteLength = 2000;
    public const int WellbeingCheckDays = 7;

    /// <summary>null — запись корректна; иначе сообщение об ошибке (для 400).</summary>
    public static string? Validate(VaccinationContent c)
    {
        var fromCatalog = c.SeriesCode is not null;
        if (fromCatalog)
        {
            if (VaccineCatalog.Find(c.SeriesCode) is null) return "Неизвестная прививка из календаря.";
            if (c.DoseIndex is null or < 0) return "Не указана доза.";
        }
        else if (string.IsNullOrWhiteSpace(c.CustomName))
        {
            return "Укажите название прививки.";
        }
        else if (c.CustomName.Length > MaxCustomNameLength)
        {
            return "Слишком длинное название прививки.";
        }

        if (c.VaccineName is { Length: > MaxVaccineNameLength }) return "Слишком длинное название вакцины.";

        if (c.Kind == VaccinationKind.Unknown && c.Date is not null)
            return "У ответа «не помню» не может быть даты.";

        if (c.Date is { } date)
        {
            if (c.DatePrecision is null) return "Не указана точность даты.";
            if (date > c.Today) return "Дата не может быть в будущем.";
            if (c.BirthDate is { } birth && date < birth) return "Дата раньше даты рождения.";
        }

        return null;
    }
}
