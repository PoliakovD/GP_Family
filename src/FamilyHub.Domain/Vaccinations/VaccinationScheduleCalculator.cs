namespace FamilyHub.Domain.Vaccinations;

public enum VaccinationStatus
{
    Done,
    HadDisease,
    DueSoon,
    CanDo,
    Upcoming,
    NoData,
}

/// <summary>
/// Один пункт графика — конкретная доза серии (фиксированная, сгенерированный повтор или текущий
/// сезон) с вычисленным статусом. <see cref="DoseIndex"/>: индекс в <c>VaccineSeries.Doses</c> для
/// фиксированных доз, <c>Doses.Count + k</c> для k-го (с нуля) повтора после них, -1 — сентинел
/// «вся серия закрыта перенесённой болезнью» (только у <c>ClosedByDisease</c>-серий).
/// </summary>
public record ScheduleItem(
    string SeriesCode,
    int DoseIndex,
    string Label,
    string SeriesName,
    string SeriesShortName,
    VaccineGroup Group,
    /// <summary>Возрастной этап для группировки в UI: "0-1"/"1-2"/"2-6"/"6-7"/"14+"/"adult"/
    /// "epidemic"/"closed".</summary>
    string Stage,
    VaccinationStatus Status,
    DateOnly? WindowFrom,
    DateOnly? WindowTo,
    Guid? RecordId,
    DateOnly? Date,
    bool IsRepeat);

/// <summary>
/// Строит график прививок по каталогу (<see cref="VaccineCatalog"/>), дате рождения и уже внесённым
/// фактам — чистая функция, детерминированная по (birthDate, facts, today), как
/// <c>DoseScheduleExpander</c> у курсов приёма лекарств.
///
/// «Нет данных» ≠ «пропущено»: окно, закрывшееся больше года назад без единого факта, получает
/// <see cref="VaccinationStatus.NoData"/>, а не бесконечно длящийся <see cref="VaccinationStatus.CanDo"/> —
/// взрослые редко помнят детские прививки, и жёлтая/просроченная плашка с первого дня использования
/// была бы недостоверной тревогой (см. макет, раздел «UX-решения»: «Нет данных ≠ пропущено»).
/// Эпидемические серии добровольны и появляются в графике только после первой внесённой дозы —
/// иначе у каждого человека сразу висело бы полтора десятка незаполненных пунктов «клещевой
/// энцефалит», «гепатит A» и т.д.
/// </summary>
public static class VaccinationScheduleCalculator
{
    private const int DueSoonDays = 60;
    private const int StaleDays = 365;

    public static IReadOnlyList<ScheduleItem> Calculate(DateOnly birthDate, IReadOnlyList<VaccinationFact> facts, DateOnly today)
    {
        var bySeries = facts.GroupBy(f => f.SeriesCode).ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<ScheduleItem>();
        foreach (var series in VaccineCatalog.All)
            result.AddRange(CalculateSeries(series, birthDate, bySeries.GetValueOrDefault(series.Code, []), today));
        return result;
    }

    private static IEnumerable<ScheduleItem> CalculateSeries(
        VaccineSeries series, DateOnly birthDate, List<VaccinationFact> facts, DateOnly today)
    {
        if (series.SeasonalWindow is { } season)
        {
            var item = SeasonalItem(series, season, birthDate, facts, today);
            if (item is not null) yield return item;
            yield break;
        }

        if (series.ClosedByDisease)
        {
            var disease = facts.FirstOrDefault(f => f.Kind == VaccinationKind.HadDisease);
            if (disease is not null)
            {
                yield return new ScheduleItem(
                    series.Code, -1, series.Name, series.Name, series.ShortName, series.Group, "closed",
                    VaccinationStatus.HadDisease, null, disease.Date, disease.RecordId, disease.Date, false);
                yield break;
            }
        }

        // Эпидемические серии добровольны: доза 1 не предлагается, пока пользователь сам не внёс хоть что-то по серии.
        if (series.Group == VaccineGroup.Epidemic && facts.Count == 0) yield break;

        DateOnly? previousDate = null;
        for (var i = 0; i < series.Doses.Count; i++)
        {
            var dose = series.Doses[i];
            var fact = facts.FirstOrDefault(f => f.DoseIndex == i);

            if (!dose.IsAgeBased && previousDate is null)
            {
                if (fact is null) yield break; // цепочка прервана: предыдущая доза не отмечена — дальше не идём
                // Доза отмечена «вне очереди» (например, задним числом без предыдущей) — покажем факт без окна.
                yield return BuildItem(series, i, dose.Label, series.Name, series.ShortName, "epidemic", null, null, fact, today, false);
                if (fact.Kind != VaccinationKind.Done) yield break;
                previousDate = fact.Date;
                continue;
            }

            var from = dose.IsAgeBased ? dose.AgeFrom?.AddTo(birthDate) : dose.IntervalFrom?.AddTo(previousDate!.Value);
            var to = dose.IsAgeBased ? dose.AgeTo?.AddTo(birthDate) : dose.IntervalTo?.AddTo(previousDate!.Value);
            var stage = dose.IsAgeBased ? StageOf(series, dose.AgeFrom) : "epidemic";

            yield return BuildItem(series, i, dose.Label, series.Name, series.ShortName, stage, from, to, fact, today, false);

            if (fact is { Kind: VaccinationKind.Done }) previousDate = fact.Date ?? to ?? from;
            else if (!dose.IsAgeBased) yield break; // Unknown/HadDisease/нет факта — дальше по интервальной цепочке идти нельзя
        }

        if (series.RepeatEveryYears is { } years && previousDate is not null)
        {
            var repeatIndex = series.Doses.Count;
            var anchor = previousDate.Value;
            var name = series.RepeatName ?? series.Name;
            var shortName = series.RepeatShortName ?? series.ShortName;
            var stage = series.Group == VaccineGroup.Epidemic ? "epidemic" : "adult";

            while (true)
            {
                var to = anchor.AddYears(years);
                var fact = facts.FirstOrDefault(f => f.DoseIndex == repeatIndex);
                yield return BuildItem(series, repeatIndex, "Ревакцинация", name, shortName, stage, null, to, fact, today, true);

                if (fact is not { Kind: VaccinationKind.Done }) yield break; // дальше показываем только ближайший неотмеченный повтор
                anchor = fact.Date ?? to;
                repeatIndex++;
            }
        }
    }

    /// <summary>Ежегодная сезонная прививка (грипп) — не через RepeatEveryYears (тот считает годы от
    /// предыдущей дозы), а через календарный сезон: доза, сделанная в любой момент между началом
    /// этого сезона и началом следующего, засчитывается на весь год.</summary>
    private static ScheduleItem? SeasonalItem(
        VaccineSeries series, (int FromMonth, int ToMonth) season, DateOnly birthDate, List<VaccinationFact> facts, DateOnly today)
    {
        var dose = series.Doses[0];
        var eligibleFrom = dose.AgeFrom?.AddTo(birthDate) ?? birthDate;
        if (today < eligibleFrom) return null;

        var seasonYear = today.Month >= season.FromMonth ? today.Year : today.Year - 1;
        var windowFrom = new DateOnly(seasonYear, season.FromMonth, 1);
        var windowTo = new DateOnly(seasonYear, season.ToMonth, DateTime.DaysInMonth(seasonYear, season.ToMonth));
        var nextWindowFrom = windowFrom.AddYears(1);

        var done = facts
            .Where(f => f.Kind == VaccinationKind.Done && f.Date is { } d && d >= windowFrom && d < nextWindowFrom)
            .OrderByDescending(f => f.Date)
            .FirstOrDefault();

        var status = done is not null
            ? VaccinationStatus.Done
            : today < windowFrom.AddDays(-DueSoonDays) ? VaccinationStatus.Upcoming
            : today <= windowTo ? VaccinationStatus.DueSoon
            : VaccinationStatus.CanDo;

        return new ScheduleItem(
            series.Code, 0, dose.Label, series.Name, series.ShortName, series.Group, "adult", status,
            windowFrom, windowTo, done?.RecordId, done?.Date, false);
    }

    private static ScheduleItem BuildItem(
        VaccineSeries series, int doseIndex, string label, string seriesName, string seriesShortName, string stage,
        DateOnly? from, DateOnly? to, VaccinationFact? fact, DateOnly today, bool isRepeat)
    {
        if (fact is { Kind: VaccinationKind.HadDisease })
            return new ScheduleItem(series.Code, doseIndex, label, seriesName, seriesShortName, series.Group, stage,
                VaccinationStatus.HadDisease, from, to, fact.RecordId, fact.Date, isRepeat);

        if (fact is { Kind: VaccinationKind.Done })
            return new ScheduleItem(series.Code, doseIndex, label, seriesName, seriesShortName, series.Group, stage,
                VaccinationStatus.Done, from, to, fact.RecordId, fact.Date, isRepeat);

        if (fact is { Kind: VaccinationKind.Unknown })
            return new ScheduleItem(series.Code, doseIndex, label, seriesName, seriesShortName, series.Group, stage,
                VaccinationStatus.NoData, from, to, fact.RecordId, null, isRepeat);

        return new ScheduleItem(series.Code, doseIndex, label, seriesName, seriesShortName, series.Group, stage,
            StatusFromWindow(from, to, today), from, to, null, null, isRepeat);
    }

    /// <summary>Окно ещё не открылось → Upcoming; открыто (или в последние 60 дней перед сроком) →
    /// DueSoon; закрылось не больше года назад → CanDo («срок прошёл — это не страшно»); закрылось
    /// больше года назад без единого факта → NoData («нет данных», не просрочка).</summary>
    private static VaccinationStatus StatusFromWindow(DateOnly? from, DateOnly? to, DateOnly today)
    {
        var due = to ?? from;
        if (due is null) return VaccinationStatus.Upcoming;

        var dueDate = due.Value;
        var opensAt = from ?? dueDate;
        var leadStart = dueDate.AddDays(-DueSoonDays);

        if (today < opensAt && today < leadStart) return VaccinationStatus.Upcoming;
        if (today <= dueDate) return VaccinationStatus.DueSoon;

        var daysLate = today.DayNumber - dueDate.DayNumber;
        return daysLate <= StaleDays ? VaccinationStatus.CanDo : VaccinationStatus.NoData;
    }

    private static string StageOf(VaccineSeries series, AgeSpan? ageFrom)
    {
        if (series.Group != VaccineGroup.National) return series.Group == VaccineGroup.Epidemic ? "epidemic" : "adult";
        return (ageFrom?.Years ?? 0) switch
        {
            0 => "0-1",
            1 => "1-2",
            >= 2 and <= 5 => "2-6",
            6 or 7 => "6-7",
            _ => "14+",
        };
    }
}
