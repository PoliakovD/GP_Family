namespace FamilyHub.TelegramBot.Configuration;

/// <summary>
/// Секция "Messaging" — та же форма (и те же env-переменные Messaging__Kafka__*/Messaging__Retry__*),
/// что у FamilyHub.Infrastructure.Messaging.MessagingOptions на стороне Api, но без Outbox/
/// ExtraConsumerAssemblies: у бота нет EF outbox (нет БД, см. Messaging/BotMessagingRegistration.cs)
/// и нет сканирования сборок потребителей (единственный потребитель — TelegramOutboundConsumer,
/// регистрируется явно). Enabled=false (локальная разработка без брокера) — бот обслуживает
/// только вебхук, без единого обращения к Kafka.
/// </summary>
public class BotMessagingOptions
{
    public const string SectionName = "Messaging";

    public KafkaSettings Kafka { get; set; } = new();

    public RetrySettings Retry { get; set; } = new();

    public class KafkaSettings
    {
        public bool Enabled { get; set; }

        public string BootstrapServers { get; set; } = string.Empty;
    }

    public class RetrySettings
    {
        public int RetryLimit { get; set; } = 5;
        public TimeSpan MinInterval { get; set; } = TimeSpan.FromSeconds(1);
        public TimeSpan MaxInterval { get; set; } = TimeSpan.FromMinutes(1);
        public TimeSpan IntervalDelta { get; set; } = TimeSpan.FromSeconds(5);
    }
}
