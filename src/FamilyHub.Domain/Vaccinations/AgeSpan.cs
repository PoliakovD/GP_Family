namespace FamilyHub.Domain.Vaccinations;

/// <summary>
/// Точный возраст в календарных единицах (года/месяцы/дни), а не приближение сплошными днями —
/// применяется через <see cref="DateOnly.AddYears"/>/<see cref="DateOnly.AddMonths"/>/
/// <see cref="DateOnly.AddDays"/>, поэтому «6 лет» от даты рождения остаётся ровно 6 годовщиной
/// независимо от високосных лет, в отличие от умножения на 365.
/// </summary>
public readonly record struct AgeSpan(int Years, int Months, int Days)
{
    public static readonly AgeSpan Zero = new(0, 0, 0);

    public static AgeSpan FromYears(int years) => new(years, 0, 0);
    public static AgeSpan FromMonths(int months) => new(0, months, 0);
    public static AgeSpan FromDays(int days) => new(0, 0, days);

    public DateOnly AddTo(DateOnly date) => date.AddYears(Years).AddMonths(Months).AddDays(Days);
}
