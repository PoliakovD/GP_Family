using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Outbox;

/// <summary>
/// Фоновый цикл доставки outbox → MediatR. Любое исключение прохода логируется и не
/// роняет сервис: гарантия доставки держится на том, что диспетчер жив всегда,
/// а сбойные строки переигрываются по backoff'у из OutboxProcessor.
/// </summary>
public class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OutboxDispatcher запущен (poll {PollInterval})", options.Value.PollInterval);

        var lastPurge = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
                processed = await processor.ProcessBatchAsync(stoppingToken);

                // Ретеншн ПДн в Payload: периодически чистим давно обработанные строки.
                if (DateTime.UtcNow - lastPurge >= options.Value.PurgeInterval)
                {
                    await processor.PurgeProcessedAsync(stoppingToken);
                    lastPurge = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Сбой прохода outbox-диспетчера, продолжаем после паузы");
            }

            // Полный батч — в очереди, вероятно, есть ещё: продолжаем без паузы.
            if (processed >= options.Value.BatchSize) continue;

            try
            {
                await Task.Delay(options.Value.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("OutboxDispatcher остановлен");
    }
}
