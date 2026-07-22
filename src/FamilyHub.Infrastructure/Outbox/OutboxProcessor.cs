using System.Text.Json;
using FamilyHub.Contracts.Events;
using FamilyHub.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Outbox;

/// <summary>
/// Один проход по outbox: выборка недоставленных строк, десериализация и Publish в MediatR,
/// отметка результата. Вынесен из OutboxDispatcher, чтобы dev-эндпоинт и интеграционные
/// тесты могли прогнать доставку синхронно, без ожидания фонового цикла.
/// Однонстансовое развёртывание — без FOR UPDATE SKIP LOCKED; при переходе на несколько
/// реплик выборку нужно перевести на raw SQL с SKIP LOCKED.
/// </summary>
public class OutboxProcessor(
    AppDbContext db,
    IPublisher publisher,
    EventTypeRegistry registry,
    IOptions<OutboxOptions> options,
    ILogger<OutboxProcessor> logger)
{
    public async Task<int> ProcessBatchAsync(CancellationToken ct = default)
    {
        var opts = options.Value;
        var now = DateTime.UtcNow;

        // AsNoTracking + ExecuteUpdate ниже: хендлеры делят этот же scoped AppDbContext,
        // и их операции с ChangeTracker'ом (вплоть до Clear при гонке DedupKey) не должны
        // влиять на отметку результата обработки строки.
        var batch = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.ProcessedAt == null
                && m.Attempts < opts.MaxAttempts
                && (m.NextAttemptAt == null || m.NextAttemptAt <= now))
            .OrderBy(m => m.OccurredAt)
            .Take(opts.BatchSize)
            .ToListAsync(ct);

        if (batch.Count == 0) return 0;

        logger.LogDebug("Outbox: обработка {Count} строк(и)", batch.Count);

        var processed = 0;
        foreach (var message in batch)
        {
            try
            {
                var eventType = registry.Resolve(message.Type);
                var domainEvent = (IDomainEvent)JsonSerializer.Deserialize(message.Payload, eventType, OutboxWriter.JsonOptions)!;

                await publisher.Publish(domainEvent, ct);

                await db.OutboxMessages.Where(m => m.Id == message.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.ProcessedAt, DateTime.UtcNow)
                        .SetProperty(m => m.Error, (string?)null), ct);
                processed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Ровно один инкремент на проход даже при гонке прогонов: инкремент атомарный, в БД.
                var attempts = message.Attempts + 1;
                var nextAttemptAt = DateTime.UtcNow.AddSeconds(Math.Pow(2, attempts) * opts.RetryBaseDelaySeconds);

                logger.LogError(ex,
                    "Outbox: событие {EventId} ({Type}) не доставлено, попытка {Attempts}/{Max}",
                    message.Id, message.Type, attempts, opts.MaxAttempts);

                await db.OutboxMessages.Where(m => m.Id == message.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.Attempts, m => m.Attempts + 1)
                        .SetProperty(m => m.NextAttemptAt, nextAttemptAt)
                        .SetProperty(m => m.Error, ex.ToString()), ct);
            }
        }

        return processed;
    }

    /// <summary>
    /// Удаляет обработанные строки старше ProcessedRetention: Payload содержит снимки
    /// событий (в т.ч. ПДн) и не должен храниться бессрочно. Вызывается диспетчером.
    /// </summary>
    public async Task<int> PurgeProcessedAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - options.Value.ProcessedRetention;
        var purged = await db.OutboxMessages
            .Where(m => m.ProcessedAt != null && m.ProcessedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        if (purged > 0)
            logger.LogInformation("Outbox: удалено {Count} обработанных строк старше {Cutoff}", purged, cutoff);
        return purged;
    }
}
