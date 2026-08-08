using MassTransit;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Infrastructure.Messaging.Kafka;

/// <summary>
/// Мост «внутренняя шина → Kafka» (ADR-0006). EF Core Outbox (UseBusOutbox) перехватывает
/// scoped IPublishEndpoint основной шины, но НЕ ITopicProducer&lt;T&gt; райдера — Rider это
/// отдельный IBusInstance со своей абстракцией продюсера, вне транзакционности outbox.
/// Поэтому долговечность доставки якорится там, где уже решена (бизнес-запись + outbox-строка
/// атомарны; outbox → InMemory-шина устойчива до доставки), а сама пересылка в Kafka —
/// обычный at-least-once потребитель основной шины: единственная задача — переложить событие
/// в топик. Потребители Kafka обязаны быть идемпотентными по контракту — дубль при ретрае
/// этого потребителя допустим.
/// </summary>
public class KafkaTopicBridgeConsumer<T>(ITopicProducer<T> producer, ILogger<KafkaTopicBridgeConsumer<T>> logger)
    : IConsumer<T> where T : class
{
    public async Task Consume(ConsumeContext<T> context)
    {
        await producer.Produce(context.Message, context.CancellationToken);
        logger.LogDebug("Событие {Event} ({MessageId}) отправлено в Kafka", typeof(T).Name, context.MessageId);
    }
}
