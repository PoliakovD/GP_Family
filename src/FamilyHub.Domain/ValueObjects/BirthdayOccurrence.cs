namespace FamilyHub.Domain.ValueObjects;

/// <summary>
/// День рождения повторяется ежегодно — общая математика "ближайшее наступление / сколько дней
/// осталось / сколько лет исполнится". Нужна двум независимым потребителям в разных слоях:
/// <c>FamilyHub.Infrastructure.Notifications.ReminderScanJob</c> (окно предупреждения о ДР) и
/// <c>FamilyHub.Api.Features.Home.HomeSummaryService</c> (блок "Требует внимания" на Главной,
/// редизайн v2) — поэтому здесь, а не дублированием в каждом (см. patterns/backend.md,
/// "Разделяемая политика формата — в Domain, не в сервисе": как только правило нужно двум
/// потребителям в разных слоях, дублирование почти сразу расходится).
/// </summary>
public static class BirthdayOccurrence
{
    /// <summary>Ближайшая (в этом или следующем году) календарная дата дня рождения от today.</summary>
    public static DateOnly NextOccurrence(DateOnly birthDate, DateOnly today)
    {
        var candidate = SafeDate(today.Year, birthDate.Month, birthDate.Day);
        return candidate < today ? SafeDate(today.Year + 1, birthDate.Month, birthDate.Day) : candidate;
    }

    /// <summary>Сколько дней осталось до ближайшего наступления (0 — сегодня).</summary>
    public static int DaysUntil(DateOnly birthDate, DateOnly today) =>
        NextOccurrence(birthDate, today).DayNumber - today.DayNumber;

    /// <summary>Сколько лет исполнится в ближайшее наступление.</summary>
    public static int TurningAge(DateOnly birthDate, DateOnly today) =>
        NextOccurrence(birthDate, today).Year - birthDate.Year;

    /// <summary>29 февраля в невисокосный год переносится на 28-е, а не падает с исключением.</summary>
    public static DateOnly SafeDate(int year, int month, int day) =>
        new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));
}
