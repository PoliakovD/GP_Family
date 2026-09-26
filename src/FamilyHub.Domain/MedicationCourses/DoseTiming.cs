using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.MedicationCourses;

/// <summary>Итог приёма для истории и статистики: цвета сетки «вовремя / с опозданием / пропущен / впереди».</summary>
public enum DoseOutcome
{
    /// <summary>Ещё не наступил.</summary>
    Upcoming = 0,

    /// <summary>Наступил и ждёт отметки, срок пропуска не вышел.</summary>
    Due = 1,

    OnTime = 2,
    Late = 3,
    Missed = 4,

    /// <summary>Осознанный пропуск: в статистику вовремя не входит, семье не сообщается.</summary>
    Skipped = 5,
}

/// <summary>Временные правила приёма: что считать опозданием и когда приём становится пропущенным.</summary>
public static class DoseTiming
{
    /// <summary>Принят позже чем через столько после планового времени — «с опозданием».</summary>
    public static readonly TimeSpan LateAfter = TimeSpan.FromMinutes(60);

    /// <summary>Запас после «Отложить»: отложенный приём не становится пропущенным раньше, чем через
    /// это время после конца отсрочки.</summary>
    public static readonly TimeSpan SnoozeGrace = TimeSpan.FromMinutes(30);

    public static DateTime MissedDeadline(DateTime scheduledAtUtc, int missedAfterMinutes, DateTime? snoozedUntilUtc = null)
    {
        var deadline = scheduledAtUtc.AddMinutes(missedAfterMinutes);
        if (snoozedUntilUtc is { } s && s + SnoozeGrace > deadline) deadline = s + SnoozeGrace;
        return deadline;
    }

    /// <summary>Итог приёма. <paramref name="status"/> null — строки нет (плановый приём без действий).</summary>
    public static DoseOutcome Classify(DoseStatus? status, DateTime scheduledAtUtc, DateTime? takenAtUtc,
        int missedAfterMinutes, DateTime? snoozedUntilUtc, DateTime nowUtc)
    {
        switch (status)
        {
            case DoseStatus.Taken:
                return takenAtUtc is { } t && t - scheduledAtUtc > LateAfter ? DoseOutcome.Late : DoseOutcome.OnTime;
            case DoseStatus.Skipped:
                return DoseOutcome.Skipped;
            case DoseStatus.Missed:
                return DoseOutcome.Missed;
            default:
                if (nowUtc >= MissedDeadline(scheduledAtUtc, missedAfterMinutes, snoozedUntilUtc)) return DoseOutcome.Missed;
                return nowUtc >= scheduledAtUtc ? DoseOutcome.Due : DoseOutcome.Upcoming;
        }
    }

    /// <summary>Доля приёмов «вовремя» среди учитываемых (осознанные пропуски и будущее не считаются).
    /// null — считать нечего.</summary>
    public static int? OnTimePercent(IEnumerable<DoseOutcome> outcomes)
    {
        int onTime = 0, counted = 0;
        foreach (var o in outcomes)
        {
            if (o is DoseOutcome.Upcoming or DoseOutcome.Due or DoseOutcome.Skipped) continue;
            counted++;
            if (o == DoseOutcome.OnTime) onTime++;
        }
        return counted == 0 ? null : (int)Math.Round(100.0 * onTime / counted, MidpointRounding.AwayFromZero);
    }
}
