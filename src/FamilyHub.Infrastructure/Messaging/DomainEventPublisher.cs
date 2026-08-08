using MassTransit;

namespace FamilyHub.Infrastructure.Messaging;

public class DomainEventPublisher(IPublishEndpoint publishEndpoint) : IDomainEventPublisher
{
    public Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default) where TEvent : class =>
        publishEndpoint.Publish(domainEvent, ct);
}
