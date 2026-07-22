using System.Text.Json;
using FamilyHub.Contracts.Events;
using FamilyHub.Infrastructure.Persistence;

namespace FamilyHub.Infrastructure.Outbox;

public class OutboxWriter(AppDbContext db, EventTypeRegistry registry) : IOutboxWriter
{
    // Дефолтные опции STJ: контракты — простые record'ы, camelCase не нужен,
    // главное — стабильность формата между записью и чтением (OutboxProcessor).
    internal static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Default;

    public void Enqueue(IDomainEvent domainEvent)
    {
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = domainEvent.EventId,
            Type = registry.GetName(domainEvent.GetType()),
            Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), JsonOptions),
            OccurredAt = domainEvent.OccurredAt,
        });
    }
}
