using FamilyHub.Infrastructure.Messaging;

namespace FamilyHub.UnitTests.TestSupport;

/// <summary>
/// Заглушка IDomainEventPublisher для юнит-тестов, которым не нужна цепочка "событие →
/// потребитель" (большинство — проверяют только сам факт и содержимое публикации, эффекты
/// потребителей покрыты интеграционными тестами или DomainEventTestPipeline). Замена
/// .Writer из удалённого OutboxTestPipeline (ADR-0006).
/// </summary>
public sealed class RecordingDomainEventPublisher : IDomainEventPublisher
{
    public List<object> Published { get; } = [];

    public Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default) where TEvent : class
    {
        Published.Add(domainEvent);
        return Task.CompletedTask;
    }
}
