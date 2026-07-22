using FamilyHub.Infrastructure.Notifications;
using FamilyHub.Infrastructure.Outbox;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Birthdays;
using FamilyHub.Modules.Medical;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace FamilyHub.UnitTests.TestSupport;

/// <summary>
/// Мини-конвейер outbox для юнит-тестов: реальные MediatR-хендлеры (Infrastructure + модули),
/// IsolatingLoggingPublisher и OutboxProcessor поверх тестового SQLite-AppDbContext.
/// Позволяет проверять сквозные инварианты «сервис → событие → хендлер» без Testcontainers.
/// </summary>
public sealed class OutboxTestPipeline
{
    private readonly ServiceProvider _provider;

    public OutboxTestPipeline(AppDbContext db, INotificationSender? sender = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(db);
        services.AddSingleton(sender ?? Substitute.For<INotificationSender>());
        services.AddSingleton<EventTypeRegistry>();
        services.AddSingleton<IOutboxWriter, OutboxWriter>();
        services.AddSingleton<NotificationSendingService>();
        services.AddSingleton<OutboxProcessor>();
        services.Configure<OutboxOptions>(_ => { });
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssemblies(
                typeof(OutboxProcessor).Assembly,
                typeof(MedicalModule).Assembly,
                typeof(BirthdayModule).Assembly);
            cfg.NotificationPublisherType = typeof(IsolatingLoggingPublisher);
        });

        _provider = services.BuildServiceProvider();
    }

    public IOutboxWriter Writer => _provider.GetRequiredService<IOutboxWriter>();

    public NotificationSendingService Notifications => _provider.GetRequiredService<NotificationSendingService>();

    /// <summary>Синхронный прогон одной пачки outbox — аналог фонового цикла OutboxDispatcher.</summary>
    public Task<int> DispatchAsync(CancellationToken ct = default) =>
        _provider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(ct);
}
