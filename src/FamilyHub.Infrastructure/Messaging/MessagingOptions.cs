namespace FamilyHub.Infrastructure.Messaging;

/// <summary>
/// Настройки шины (ADR-0006): EF Core Outbox поверх Postgres (доставка на внутреннюю
/// InMemory-шину) + опциональный Kafka Rider (внешний фан-аут каждого события в свой топик).
/// Тот же идиом конфиг-переключателя, что Enrichment:Provider — Kafka:Enabled=false даёт
/// чистый InMemory-режим без единого обращения к брокеру.
/// </summary>
public class MessagingOptions
{
    public const string SectionName = "Messaging";

    public OutboxSettings Outbox { get; set; } = new();

    public KafkaSettings Kafka { get; set; } = new();

    public RetrySettings Retry { get; set; } = new();

    /// <summary>
    /// Доп. сборки для сканирования потребителей — только для тестов (см.
    /// MessagingFailureIsolationTests): позволяет зарегистрировать тестовый consumer-двойник
    /// без второго вызова AddMassTransit (нельзя) и без прод-риска (пусто по умолчанию).
    /// Имена сборок через запятую, резолвятся Assembly.Load.
    /// </summary>
    public string? ExtraConsumerAssemblies { get; set; }

    public class OutboxSettings
    {
        /// <summary>Пауза опроса outbox-таблицы, когда доставлять нечего.</summary>
        public TimeSpan QueryDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>Максимум строк, забираемых за один проход.</summary>
        public int QueryMessageLimit { get; set; } = 20;

        /// <summary>Окно подавления дублей на уровне шины (MessageId).</summary>
        public TimeSpan DuplicateDetectionWindow { get; set; } = TimeSpan.FromMinutes(30);
    }

    public class KafkaSettings
    {
        /// <summary>false — Rider и мосты вообще не регистрируются, брокер не нужен.</summary>
        public bool Enabled { get; set; }

        public string BootstrapServers { get; set; } = string.Empty;
    }

    /// <summary>
    /// Per-consumer ретрай (UseMessageRetry) — замена прежнему IsolatingLoggingPublisher: падение
    /// одного потребителя не трогает соседей, ретраится только его receive endpoint. Дефолты —
    /// прод-значения; MessagingFailureIsolationTests сжимает интервалы, чтобы не ждать
    /// десятки секунд реального exponential backoff в проверке "сбойный потребитель
    /// dead-letter'ится после исчерпания попыток".
    /// </summary>
    public class RetrySettings
    {
        public int RetryLimit { get; set; } = 5;
        public TimeSpan MinInterval { get; set; } = TimeSpan.FromSeconds(1);
        public TimeSpan MaxInterval { get; set; } = TimeSpan.FromMinutes(1);
        public TimeSpan IntervalDelta { get; set; } = TimeSpan.FromSeconds(5);
    }
}
