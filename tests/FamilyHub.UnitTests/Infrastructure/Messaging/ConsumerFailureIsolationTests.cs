using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Messaging;

/// <summary>
/// Раньше это гарантировал наш код — IsolatingLoggingPublisherTests проверял, что кастомный
/// INotificationPublisher прогоняет все хендлеры даже при падении одного. С MassTransit это
/// свойство топологии (один receive endpoint на потребителя), а не нашего кода (см. ADR-0006) —
/// но заслуживает лёгкого исполняемого теста, а не только доверия к документации библиотеки.
/// Throwaway-тип сообщения и харнесс без БД — проверяем сам факт изоляции, не бизнес-логику.
///
/// ADR-0007: этот харнесс — InMemory (у Kafka Rider нет in-memory тестового харнесса вообще) —
/// подтверждает изоляцию сбоя на InMemory-топологии (ConfigureConsumers на одном receive
/// endpoint), которая и сегодня используется в dev-lite-режиме (Messaging:Kafka:Enabled=false).
/// Для прод-топологии (Kafka Rider, Messaging:Kafka:Enabled=true) изоляция сбоя гарантируется
/// независимостью Kafka consumer group у каждого TopicEndpoint — это проверяет
/// KafkaBridgeFlowTests.FailingConsumer_OnKafka_DoesNotBlockIndependentConsumerGroup
/// (KafkaIntegrationCollection, Testcontainers.Kafka), не этот тест.
/// </summary>
public class ConsumerFailureIsolationTests
{
    public record ProbeMessage(Guid Id);

    public class FailingConsumer : IConsumer<ProbeMessage>
    {
        public Task Consume(ConsumeContext<ProbeMessage> context) =>
            throw new InvalidOperationException("Тестовый сбой потребителя");
    }

    public class RecordingConsumer(RecordedMessages recorded) : IConsumer<ProbeMessage>
    {
        public Task Consume(ConsumeContext<ProbeMessage> context)
        {
            recorded.Received.Add(context.Message.Id);
            return Task.CompletedTask;
        }
    }

    public class RecordedMessages
    {
        public List<Guid> Received { get; } = [];
    }

    [Fact]
    public async Task FailingConsumer_DoesNotBlockNeighborConsumer_OfTheSameMessage()
    {
        var recorded = new RecordedMessages();
        await using var provider = new ServiceCollection()
            .AddSingleton(recorded)
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<FailingConsumer>();
                x.AddConsumer<RecordingConsumer>();

                x.UsingInMemory((context, cfg) =>
                {
                    // Immediate(1) вместо прод-exponential — тест проверяет изоляцию, а не
                    // тайминг ретрая (тот отдельно покрыт MessagingFailureIsolationTests).
                    cfg.UseMessageRetry(r => r.Immediate(1));
                    cfg.ConfigureEndpoints(context);
                });
            })
            .BuildServiceProvider(validateScopes: true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var id = Guid.NewGuid();
            await harness.Bus.Publish(new ProbeMessage(id));

            await Task.Delay(20);
            await harness.InactivityTask;

            var failingHarness = provider.GetRequiredService<IConsumerTestHarness<FailingConsumer>>();
            (await failingHarness.Consumed.Any<ProbeMessage>()).Should().BeTrue(
                "падающий потребитель должен был реально получить сообщение (а не быть пропущен)");

            recorded.Received.Should().ContainSingle().Which.Should().Be(id,
                "сосед-потребитель того же события должен отработать несмотря на падение другого");
        }
        finally
        {
            await harness.Stop();
        }
    }
}
