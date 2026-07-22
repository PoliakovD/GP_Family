using FamilyHub.Contracts.Events;

namespace FamilyHub.Infrastructure.Outbox;

/// <summary>
/// Отображение «короткое имя ↔ тип события» по скану сборки Contracts. Короткое имя
/// (имя record'а) хранится в колонке OutboxMessage.Type — в отличие от
/// assembly-qualified имени оно переживает рефакторинг namespace'ов и версий сборки.
/// </summary>
public class EventTypeRegistry
{
    private readonly Dictionary<string, Type> _byName = [];
    private readonly Dictionary<Type, string> _byType = [];

    public EventTypeRegistry()
    {
        var eventTypes = typeof(IDomainEvent).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDomainEvent).IsAssignableFrom(t));

        foreach (var type in eventTypes)
        {
            _byName.Add(type.Name, type);
            _byType.Add(type, type.Name);
        }
    }

    public string GetName(Type eventType) =>
        _byType.TryGetValue(eventType, out var name)
            ? name
            : throw new InvalidOperationException(
                $"Тип {eventType.FullName} не является доменным событием из сборки Contracts.");

    public Type Resolve(string name) =>
        _byName.TryGetValue(name, out var type)
            ? type
            : throw new InvalidOperationException(
                $"Неизвестное имя события «{name}» в outbox — события с таким именем нет в сборке Contracts.");
}
