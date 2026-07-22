namespace FamilyHub.Contracts.Events;

/// <summary>
/// База для событий: генерирует EventId/OccurredAt при создании, а при десериализации
/// из outbox значения восстанавливаются через init-сеттеры.
/// </summary>
public abstract record DomainEvent : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();

    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}
