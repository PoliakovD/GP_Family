using FamilyHub.Contracts.Events;

namespace FamilyHub.Infrastructure.Outbox;

/// <summary>
/// Постановка доменного события в outbox. Только добавляет строку в текущий AppDbContext
/// (без SaveChanges) — событие фиксируется тем же SaveChangesAsync, что и бизнес-данные
/// вызывающего кода, т.е. атомарно с ними (в т.ч. внутри явной BeginTransactionAsync).
/// </summary>
public interface IOutboxWriter
{
    void Enqueue(IDomainEvent domainEvent);
}
