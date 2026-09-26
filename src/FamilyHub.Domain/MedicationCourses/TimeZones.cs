namespace FamilyHub.Domain.MedicationCourses;

/// <summary>Безопасный поиск часового пояса: часовой пояс приходит из браузера и может быть
/// неизвестен серверу (нет ICU, устаревшее имя) — тогда падать нельзя.</summary>
public static class TimeZones
{
    public const string DefaultId = "Europe/Moscow";

    public static TimeZoneInfo Resolve(string? id)
    {
        foreach (var candidate in new[] { id, DefaultId })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }

    /// <summary>true — имя известно системе (для валидации того, что прислал клиент).</summary>
    public static bool IsKnown(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return false;
        try { TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return false; }
    }
}
