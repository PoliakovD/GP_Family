namespace FamilyHub.Domain.MedicationCourses;

/// <summary>Тихие часы получателя («23:00–7:00 — напоминания без звука»).</summary>
public static class QuietHours
{
    /// <summary>true, если сейчас (в часовом поясе получателя) тихие часы. Интервал может переходить
    /// через полночь; отсутствие границы или равные границы — тихие часы выключены.</summary>
    public static bool IsQuiet(TimeOnly? from, TimeOnly? to, DateTime nowUtc, TimeZoneInfo tz)
    {
        if (from is not { } f || to is not { } t || f == t) return false;
        var utc = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, tz));
        return f < t ? local >= f && local < t : local >= f || local < t;
    }
}
