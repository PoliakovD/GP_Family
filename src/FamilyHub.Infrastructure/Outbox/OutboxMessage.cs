namespace FamilyHub.Infrastructure.Outbox;

/// <summary>
/// Строка транзакционного outbox (этап 1 плана): сериализованное доменное событие,
/// записанное в одной транзакции с бизнес-данными и доставляемое OutboxDispatcher'ом.
/// Это инфраструктурная сущность, а не доменная — поэтому живёт здесь, а не в Domain.
/// </summary>
public class OutboxMessage
{
    /// <summary>PK = EventId события: одно и то же событие физически не может попасть в outbox дважды.</summary>
    public Guid Id { get; set; }

    /// <summary>Короткое стабильное имя типа события (имя record'а), резолвится EventTypeRegistry.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>JSON-снимок события (jsonb).</summary>
    public string Payload { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; }

    /// <summary>Момент успешной публикации всем хендлерам (null — ещё не доставлено).</summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>Число неудачных попыток публикации; после OutboxOptions.MaxAttempts строка — dead-letter.</summary>
    public int Attempts { get; set; }

    /// <summary>Не раньше этого момента строку можно пробовать снова (экспоненциальный backoff).</summary>
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>Текст последней ошибки — диагностика dead-letter строк.</summary>
    public string? Error { get; set; }
}
