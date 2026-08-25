using Microsoft.AspNetCore.Hosting;
using Testcontainers.Kafka;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// FamilyHubWebFactory + реальный Kafka-брокер (Testcontainers.Kafka) — единственная фабрика с
/// Messaging:Kafka:Enabled=true (ADR-0006, KafkaBridgeFlowTests). Все остальные интеграционные
/// тесты нарочно бегут без брокера (Messaging:Kafka:Enabled=false по умолчанию в
/// FamilyHubWebFactory), доказывая миграцию с MediatR независимо от Kafka. confluentinc/cp-kafka
/// (Confluent Community License) принят ТОЛЬКО здесь, для тестов (локально, не распространяется);
/// docker-compose.yml для реального запуска остаётся на apache/kafka (Apache-2.0) — см. таблицу
/// развилок в плане миграции.
/// </summary>
public class KafkaWebFactory : FamilyHubWebFactory
{
    // Testcontainers.Kafka 3.10.0 не даёт выбрать KRaft/образ явно (WithKRaft/WithImage под
    // конкретный образ появились в более поздних версиях пакета, а 4.x несовместим с уже
    // используемыми Testcontainers.PostgreSql/Minio 3.10.0, см. план миграции) — дефолтная
    // конфигурация билдера сама поднимает confluentinc/cp-kafka с embedded Zookeeper внутри
    // одного контейнера. Только для тестов — не образ docker-compose.yml (см. класс выше).
    private readonly KafkaContainer _kafka = new KafkaBuilder().Build();

    public override async Task InitializeAsync()
    {
        // Параллельно с postgres/minio/миграциями базовой фабрики — Kafka от них не зависит.
        await Task.WhenAll(base.InitializeAsync(), _kafka.StartAsync());
    }

    public override async Task DisposeAsync()
    {
        // Хост (и MassTransit-бас внутри него) должен полностью остановиться ПЕРВЫМ — иначе
        // ещё активные Kafka Rider consumers/producers ловят "Connection refused"/"brokers are
        // down" от уже удалённого контейнера во время teardown. base.DisposeAsync()
        // (FamilyHubWebFactory) в конце сам вызывает WebApplicationFactory.DisposeAsync(),
        // который останавливает хост — только после этого можно убивать брокер.
        await base.DisposeAsync();
        await _kafka.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // DIAG: temporarily disable eager DI validation to check whether "entry point exited
        // without ever building a host" is masking a real DI graph error in the Kafka Rider path.
        builder.UseDefaultServiceProvider((context, options) =>
        {
            options.ValidateOnBuild = false;
            options.ValidateScopes = false;
        });

        builder.UseSetting("Messaging:Kafka:Enabled", "true");
        builder.UseSetting("Messaging:Kafka:BootstrapServers", _kafka.GetBootstrapAddress());

        // AlwaysFailingUserLeftFamilyKafkaConsumer (см. KafkaBridgeFlowTests) — свой независимый
        // consumer group на user-left-family, всегда включён на этой фабрике. Держим ВСЮ
        // Kafka-коллекцию на одном контейнере/фикстуре (не заводим вторую KafkaWebFactory только
        // ради failure-isolation теста) — меньше параллельных Testcontainers на полном прогоне
        // интеграционных тестов, эмпирически меньше транзитных обрывов соединений Docker Desktop
        // под нагрузкой (см. историю правок QueryDelay в FamilyHubWebFactory). Безвредно для
        // остальных тестов коллекции: независимый consumer group, ни на что не влияет, кроме
        // собственных ретраев в фоне.
        builder.UseSetting("Messaging:ExtraConsumerAssemblies", GetType().Assembly.GetName().Name);
        builder.UseSetting("Messaging:Retry:RetryLimit", "2");
        builder.UseSetting("Messaging:Retry:MinInterval", "00:00:00.050");
        builder.UseSetting("Messaging:Retry:MaxInterval", "00:00:00.200");
        builder.UseSetting("Messaging:Retry:IntervalDelta", "00:00:00.050");
    }

    /// <summary>Bootstrap-адрес для сырого Confluent.Kafka-потребителя в тестах (проверка со внешней стороны моста).</summary>
    public string BootstrapServers => _kafka.GetBootstrapAddress();
}
