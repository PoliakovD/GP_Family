using FamilyHub.Contracts.Events;
using FamilyHub.Infrastructure.Outbox;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Outbox;

public class EventTypeRegistryTests
{
    private readonly EventTypeRegistry _sut = new();

    [Fact]
    public void AllContractEvents_RoundTripThroughRegistry()
    {
        // Каждое событие из Contracts должно записываться в outbox и читаться обратно —
        // забытая регистрация превратилась бы в dead-letter при первой же публикации.
        var eventTypes = typeof(IDomainEvent).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDomainEvent).IsAssignableFrom(t))
            .ToList();

        eventTypes.Should().NotBeEmpty();
        foreach (var type in eventTypes)
        {
            _sut.Resolve(_sut.GetName(type)).Should().Be(type);
        }
    }

    [Fact]
    public void Resolve_UnknownName_Throws()
    {
        var act = () => _sut.Resolve("NoSuchEvent");

        act.Should().Throw<InvalidOperationException>().WithMessage("*NoSuchEvent*");
    }

    [Fact]
    public void GetName_NonContractType_Throws()
    {
        var act = () => _sut.GetName(typeof(string));

        act.Should().Throw<InvalidOperationException>();
    }
}
