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

    /// <summary>
    /// Верхняя граница возраста для ежедневного ретрай-свипа недоставленных оповещений
    /// (см. ReminderScanJob.SendPendingAsync) — без неё строка с SentAt == null копилась бы и
    /// пыталась отправиться каждый день бесконечно (см. аудит
    /// module-review-2026-08-02/06-notifications-push-bot-outbox.md, находка 3). 7 дней —
    /// с запасом переживает многодневный сбой канала (истёкший VAPID/Bot Token и т.п.), но не
    /// долбит в мёртвый канал и не доставляет неделями устаревшее «скоро истекает».
    /// </summary>
    public int RetrySweepMaxAgeDays { get; set; } = 7;
}
