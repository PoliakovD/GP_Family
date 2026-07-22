using MediatR;

namespace FamilyHub.Contracts.Events;

/// <summary>
/// Кросс-модульное доменное событие (этап 1 плана). Публикуется через outbox в одной
/// транзакции с бизнес-данными, доставляется хендлерам асинхронно outbox-диспетчером.
/// </summary>
public interface IDomainEvent : INotification
{
    /// <summary>Уникальный идентификатор события — ключ идемпотентности доставки (PK outbox-таблицы).</summary>
    Guid EventId { get; }

    /// <summary>Момент возникновения события (UTC).</summary>
    DateTime OccurredAt { get; }
}
