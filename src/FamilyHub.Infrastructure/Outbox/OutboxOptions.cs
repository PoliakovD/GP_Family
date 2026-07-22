namespace FamilyHub.Infrastructure.Outbox;

/// <summary>Настройки outbox-диспетчера (этап 1 плана).</summary>
public class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Пауза между опросами outbox-таблицы, когда очередь пуста.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Максимум строк, обрабатываемых за один проход.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>После стольких неудачных попыток строка становится dead-letter (видна по колонке Error).</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>База экспоненциального backoff: задержка = 2^Attempts * RetryBaseDelaySeconds.</summary>
    public int RetryBaseDelaySeconds { get; set; } = 10;
}
