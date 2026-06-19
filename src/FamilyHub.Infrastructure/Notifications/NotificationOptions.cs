namespace FamilyHub.Infrastructure.Notifications;

/// <summary>Настройки фоновой джобы оповещений (этап 3 п.10 брифа).</summary>
public class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>За сколько дней до истечения срока годности лекарства предупреждать.</summary>
    public int ExpiryWarningDays { get; set; } = 30;

    /// <summary>За сколько дней до дня рождения предупреждать.</summary>
    public int BirthdayWarningDays { get; set; } = 7;

    /// <summary>Cron-выражение запуска джобы (по умолчанию — ежедневно в 08:00).</summary>
    public string Cron { get; set; } = "0 8 * * *";
}
