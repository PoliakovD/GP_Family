using FamilyHub.Contracts.Messaging;
using FamilyHub.Infrastructure.Messaging;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Messaging;

/// <summary>
/// Раньше "забыли зарегистрировать событие" ловил EventTypeRegistryTests — теперь то же самое
/// место риска переехало в явные Kafka-топик-константы (KafkaTopics.ByEventType намеренно НЕ
/// выводится рефлексией из DomainEventTypes.All, см. ADR-0006): новое доменное событие без
/// добавленной константы прошло бы мимо Kafka-фан-аута молча, если бы не этот тест.
/// </summary>
public class KafkaTopicsTests
{
    [Fact]
    public void ByEventType_CoversEveryDomainEvent()
    {
        KafkaTopics.ByEventType.Keys.Should().BeEquivalentTo(DomainEventTypes.All,
            "у каждого доменного события должен быть явный Kafka-топик — иначе Rider не сможет " +
            "его опубликовать при добавлении в AddRider (см. MassTransitRegistration)");
    }

    [Fact]
    public void ByEventType_HasNoDuplicateTopicNames()
    {
        KafkaTopics.ByEventType.Values.Should().OnlyHaveUniqueItems(
            "два события на один топик перепутали бы сообщения при чтении с внешней стороны");
    }

    [Theory]
    [InlineData(KafkaTopics.MedicalRecordShared)]
    [InlineData(KafkaTopics.UserLeftFamily)]
    [InlineData(KafkaTopics.MemberApproved)]
    [InlineData(KafkaTopics.MedicationExpiring)]
    [InlineData(KafkaTopics.BirthdayApproaching)]
    [InlineData(KafkaTopics.MedicationEnriched)]
    [InlineData(KafkaTopics.TelegramOutbound)]
    public void TopicNames_AreKebabCase(string topic)
    {
        // apache/kafka создаёт топики по имени как есть — кириллица/подчёркивания/CamelCase
        // усложнили бы grep/kafka-console-consumer в проде (см. ADR-0006, docker-compose).
        topic.Should().MatchRegex("^[a-z0-9]+(-[a-z0-9]+)*$");
    }
}
