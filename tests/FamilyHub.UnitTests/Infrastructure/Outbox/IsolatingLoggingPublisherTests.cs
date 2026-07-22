using FamilyHub.Contracts.Events;
using FamilyHub.Infrastructure.Outbox;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Outbox;

public class IsolatingLoggingPublisherTests
{
    private readonly IsolatingLoggingPublisher _sut = new(NullLogger<IsolatingLoggingPublisher>.Instance);

    private record TestEvent : DomainEvent;

    private sealed class RecordingHandler
    {
        public bool Called { get; private set; }
        public Task Handle(INotification _, CancellationToken __)
        {
            Called = true;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHandler
    {
        public Task Handle(INotification _, CancellationToken __) =>
            throw new InvalidOperationException("хендлер упал");
    }

    [Fact]
    public async Task Publish_FirstHandlerThrows_SecondStillRuns_AndAggregateIsThrown()
    {
        var throwing = new ThrowingHandler();
        var recording = new RecordingHandler();
        var executors = new[]
        {
            new NotificationHandlerExecutor(throwing, throwing.Handle),
            new NotificationHandlerExecutor(recording, recording.Handle),
        };

        var act = () => _sut.Publish(executors, new TestEvent(), CancellationToken.None);

        // Сбой первого хендлера не лишает события второго; ошибка при этом не глотается —
        // OutboxProcessor должен увидеть её и запланировать retry строки.
        var thrown = await act.Should().ThrowAsync<AggregateException>();
        thrown.Which.InnerExceptions.Should().ContainSingle()
            .Which.Should().BeOfType<InvalidOperationException>();
        recording.Called.Should().BeTrue();
    }

    [Fact]
    public async Task Publish_AllHandlersSucceed_DoesNotThrow()
    {
        var first = new RecordingHandler();
        var second = new RecordingHandler();
        var executors = new[]
        {
            new NotificationHandlerExecutor(first, first.Handle),
            new NotificationHandlerExecutor(second, second.Handle),
        };

        await _sut.Publish(executors, new TestEvent(), CancellationToken.None);

        first.Called.Should().BeTrue();
        second.Called.Should().BeTrue();
    }
}
