using FamilyHub.Infrastructure.Messaging;
using FamilyHub.Infrastructure.Notifications;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Security;
using FamilyHub.Modules.Birthdays;
using FamilyHub.Modules.Medical;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace FamilyHub.UnitTests.TestSupport;

/// <summary>
/// Мини-конвейер шины для юнит-тестов: реальные потребители (Infrastructure + модули) поверх
/// MassTransit-тестового харнесса. Проверяет сквозные инварианты «сервис → событие →
/// потребитель» без Testcontainers. НЕ покрывает EF Core Outbox — тот поддерживает
/// Postgres/SqlServer/MySql, не SQLite (см. ADR-0006); durability уровня outbox проверяют
/// только интеграционные тесты.
///
/// ADR-0007: в проде/docker-compose бизнес-потребители подписаны на Kafka Rider, не InMemory —
/// у Kafka Rider НЕТ in-memory тестового харнесса ("there is no in-memory rider implementation
/// for unit testing", подтверждено в MassTransit discussions), поэтому этот харнесс всегда
/// собирает потребителей по InMemory-ветке (Messaging:Kafka:Enabled=false, "dev-lite") —
/// он проверяет бизнес-логику потребителей в изоляции, а НЕ реальную Kafka-топологию (какие
/// потребители подписаны на какие топики/consumer group, переживает ли гонку двух потребителей
/// одного события на РАЗНЫХ Kafka consumer group). За топологию отвечают интеграционные тесты
/// KafkaIntegrationCollection (KafkaBridgeFlowTests, Testcontainers.Kafka).
///
/// Потребители работают со СВОИМ AppDbContext — отдельным физическим SqliteConnection на ту же
/// shared-cache БД, что и Db вызывающего теста (см. SqliteTestBase.ConnectionString), а не с
/// самим Db. Публикация в MassTransit — это реальная асинхронная доставка, не откладываемая до
/// SaveChanges, как было со старым outbox: без разделения соединений код, ещё выполняющийся в
/// сервисе-продюсере (например, ReminderScanJob.SendPendingAsync ПОСЛЕ публикации события из
/// ScanMedicationsAsync), и уже стартовавший потребитель гонялись бы по ОДНОМУ AppDbContext —
/// источник замеченной интермиттентной "Sequence contains no elements". Все потребители
/// намеренно посажены на один receive endpoint с ConcurrentMessageLimit=1 — чтобы вдобавок не
/// столкнуться друг с другом на этом (втором) соединении (UserLeftFamilyEvent уходит сразу
/// двум потребителям: Notifications + Medical).
/// </summary>
public sealed class DomainEventTestPipeline : IAsyncDisposable
{
    private readonly SqliteConnection _consumerConnection;
    private readonly AppDbContext _consumerDb;
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private readonly ITestHarness _harness;

    public DomainEventTestPipeline(string connectionString, IFieldCipher cipher, INotificationSender? sender = null)
    {
        _consumerConnection = new SqliteConnection(connectionString);
        _consumerConnection.Open();
        var consumerOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_consumerConnection).Options;
        _consumerDb = new AppDbContext(consumerOptions, cipher);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_consumerDb);
        services.AddSingleton(sender ?? Substitute.For<INotificationSender>());
        services.AddSingleton<NotificationSendingService>();
        services.AddScoped<IDomainEventPublisher, DomainEventPublisher>();

        services.AddMassTransitTestHarness(x =>
        {
            x.AddConsumers(
                typeof(DomainEventPublisher).Assembly,
                typeof(MedicalModule).Assembly,
                typeof(BirthdayModule).Assembly);

            x.UsingInMemory((context, cfg) =>
            {
                // Один именованный endpoint для ВСЕХ потребителей (а не ConfigureEndpoints,
                // который создал бы по endpoint'у на тип потребителя) — гарантирует
                // последовательную обработку через x.AddConsumers выше, а не только
                // per-endpoint лимит, который не защищает от гонки МЕЖДУ разными endpoint'ами.
                cfg.ReceiveEndpoint("test-harness", e =>
                {
                    e.ConcurrentMessageLimit = 1;
                    e.ConfigureConsumers(context);
                });
            });
        });

        _provider = services.BuildServiceProvider(validateScopes: true);
        _harness = _provider.GetRequiredService<ITestHarness>();
        // IDomainEventPublisher — scoped (оборачивает scoped IPublishEndpoint, см.
        // IDomainEventPublisher.cs) — резолвится из отдельного скоупа, не из root-провайдера.
        _scope = _provider.CreateScope();
        _harness.Start().GetAwaiter().GetResult();
    }

    public IDomainEventPublisher Publisher => _scope.ServiceProvider.GetRequiredService<IDomainEventPublisher>();

    public NotificationSendingService Notifications => _provider.GetRequiredService<NotificationSendingService>();

    /// <summary>Ждёт, пока шина обработает все уже опубликованные события — аналог прежнего
    /// синхронного прогона одной пачки outbox.</summary>
    public async Task DispatchAsync()
    {
        // Известная гонка тестового харнесса: InactivityTask может зарезолвиться до того, как
        // только что опубликованное сообщение зарегистрируется как "активность" (окно сразу
        // после Publish/Start) — короткая пауза даёт сообщению попасть в пайплайн первым.
        await Task.Delay(20);
        await _harness.InactivityTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.Stop();
        _scope.Dispose();
        await _provider.DisposeAsync();
        await _consumerDb.DisposeAsync();
        await _consumerConnection.DisposeAsync();
    }
}
