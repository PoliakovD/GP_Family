using Confluent.Kafka;
using Confluent.Kafka.Admin;
using FamilyHub.Contracts.Messaging;

namespace FamilyHub.TelegramBot.Messaging;

/// <summary>
/// Идемпотентное создание ЕДИНСТВЕННОГО топика, который читает бот (telegram-outbound) — копия
/// приёма из FamilyHub.Infrastructure.Messaging.MassTransitRegistration.EnsureTopicsExist (ADR-0007):
/// TopicEndpoint-потребитель на ещё не существующий топик валит весь Kafka Rider целиком
/// ("ReceiveTransport faulted"), а бот может стартовать раньше Api (которая создаёт остальные
/// топики). Дублирование кода, а не общий хелпер, — намеренное: общий хелпер потребовал бы
/// зависимости Confluent.Kafka в FamilyHub.Contracts, что запрещено комментарием в её csproj
/// (Contracts должна оставаться максимально лёгкой сборкой без пакетов).
/// retention.ms = 7 дней — тот же срок 152-ФЗ, что у остальных топиков (сообщение несёт текст
/// уведомления и ChatId — идентификатор Telegram).
/// </summary>
public static class KafkaTopicInitializer
{
    public static void EnsureTelegramOutboundTopicExists(string bootstrapServers)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();

        var spec = new TopicSpecification
        {
            Name = KafkaTopics.TelegramOutbound,
            NumPartitions = 1,
            ReplicationFactor = 1,
            Configs = new Dictionary<string, string>
            {
                ["retention.ms"] = ((long)TimeSpan.FromDays(7).TotalMilliseconds).ToString(),
            },
        };

        try
        {
            admin.CreateTopicsAsync([spec]).GetAwaiter().GetResult();
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code is ErrorCode.TopicAlreadyExists or ErrorCode.NoError))
        {
            // Топик уже создан предыдущим стартом этого же процесса/Api — норм.
        }
    }
}
