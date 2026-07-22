using MediatR;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Infrastructure.Outbox;

/// <summary>
/// Publisher с изоляцией сбоев: дефолтный ForeachAwaitPublisher останавливается на первом
/// исключении, из-за чего падение одного хендлера лишало бы события остальных подписчиков
/// (а IPipelineBehavior к notification-хендлерам не применяется вовсе). Здесь каждый хендлер
/// выполняется в своём try/catch с логированием; ошибки собираются в AggregateException,
/// которую OutboxProcessor трактует как сигнал к retry всей строки.
/// </summary>
public class IsolatingLoggingPublisher(ILogger<IsolatingLoggingPublisher> logger) : INotificationPublisher
{
    public async Task Publish(
        IEnumerable<NotificationHandlerExecutor> handlerExecutors,
        INotification notification,
        CancellationToken cancellationToken)
    {
        var eventName = notification.GetType().Name;
        List<Exception>? failures = null;

        foreach (var executor in handlerExecutors)
        {
            var handlerName = executor.HandlerInstance.GetType().Name;
            try
            {
                logger.LogDebug("Событие {Event} → хендлер {Handler}", eventName, handlerName);
                await executor.HandlerCallback(notification, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Хендлер {Handler} упал на событии {Event}", handlerName, eventName);
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
            throw new AggregateException($"Сбой {failures.Count} хендлер(ов) события {eventName}", failures);
    }
}
