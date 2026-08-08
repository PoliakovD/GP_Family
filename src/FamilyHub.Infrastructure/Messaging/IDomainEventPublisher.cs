namespace FamilyHub.Infrastructure.Messaging;

/// <summary>
/// Единственный разрешённый способ публикации доменного события (ADR-0006). Узкая обёртка
/// над scoped IPublishEndpoint нужна не ради экономии кода на 6 точках вызова, а чтобы
/// структурно исключить инъекцию IBus: EF Core Outbox подменяет именно scoped
/// IPublishEndpoint/ISendEndpointProvider — публикация через IBus его минует, и сообщение
/// уходит мимо транзакции (переживёт откат, потеряется при падении до коммита). Публикация —
/// внутри той же SaveChangesAsync/транзакции, что и бизнес-запись, вызвавшая событие.
/// </summary>
public interface IDomainEventPublisher
{
    Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default) where TEvent : class;
}
