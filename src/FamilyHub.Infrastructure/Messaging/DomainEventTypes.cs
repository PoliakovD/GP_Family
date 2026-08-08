using FamilyHub.Contracts.Events;

namespace FamilyHub.Infrastructure.Messaging;

/// <summary>
/// Все доменные события проекта — единственный источник правды для регистрации Kafka-мостов
/// (см. Kafka/KafkaTopicBridgeConsumer) и для проверки покрытия топиками (KafkaTopicsTests).
/// Раньше эту роль (без маппинга имён) частично играл EventTypeRegistry — тот отвечал ещё и
/// за маршрутизацию, которую теперь берёт на себя message URN шины.
/// </summary>
public static class DomainEventTypes
{
    public static readonly IReadOnlyList<Type> All =
        typeof(MedicalRecordSharedEvent).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true }
                        && t.Namespace == "FamilyHub.Contracts.Events")
            .OrderBy(t => t.Name)
            .ToList();
}
