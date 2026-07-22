using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Infrastructure.Notifications;

/// <summary>
/// Общая логика создания и доставки оповещений, извлечённая из ReminderScanJob (этап 1 плана):
/// идемпотентная вставка (UNIQUE по DedupKey) + немедленная отправка через INotificationSender.
/// Используется event-хендлерами (мгновенные алерты) и самой джобой (ежедневный ретрай-свип).
/// </summary>
public class NotificationSendingService(
    AppDbContext db,
    INotificationSender sender,
    ILogger<NotificationSendingService> logger)
{
    /// <summary>
    /// Фан-аут оповещения по получателям: для каждого — идемпотентная вставка и попытка
    /// отправки. Сбой отправки не прерывает остальных: строка остаётся с SentAt == null,
    /// её доберёт ежедневный свип ReminderScanJob.SendPendingAsync.
    /// </summary>
    public async Task NotifyAsync(
        IReadOnlyCollection<Guid> userIds, Guid familyId, NotificationType type,
        string title, string body, Guid relatedEntityId, Func<Guid, string> dedupKeyFor,
        CancellationToken ct = default)
    {
        var sentAny = false;
        foreach (var userId in userIds)
        {
            var notification = await AddIfNewAsync(userId, familyId, type, title, body, relatedEntityId, dedupKeyFor(userId), ct);
            if (notification is null) continue;

            await TrySendAsync(notification, ct);
            sentAny = true;
        }

        if (sentAny)
            await db.SaveChangesAsync(ct); // фиксация проставленных SentAt
    }

    /// <summary>Вставка с защитой от дублей: гонка по UNIQUE DedupKey не считается ошибкой.</summary>
    public async Task<Notification?> AddIfNewAsync(
        Guid userId, Guid familyId, NotificationType type, string title, string body,
        Guid relatedEntityId, string dedupKey, CancellationToken ct = default)
    {
        if (await db.Notifications.AnyAsync(n => n.DedupKey == dedupKey, ct)) return null;

        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FamilyId = familyId,
            Type = type,
            Title = title,
            Body = body,
            RelatedEntityId = relatedEntityId,
            DedupKey = dedupKey,
            CreatedAt = DateTime.UtcNow,
        };
        db.Notifications.Add(notification);

        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogDebug(
                "Создано оповещение {Type} для пользователя {UserId} (семья {FamilyId}, DedupKey={DedupKey})",
                type, userId, familyId, dedupKey);
            return notification;
        }
        catch (DbUpdateException ex)
        {
            // Параллельный прогон вставил тот же DedupKey раньше нас — UNIQUE-индекс и есть
            // страховка идемпотентности. Detach только своей записи, а не Clear всего трекера:
            // хендлеры делят scoped AppDbContext с OutboxProcessor.
            logger.LogDebug(ex, "Гонка при создании оповещения DedupKey={DedupKey}, пропускаем", dedupKey);
            db.Entry(notification).State = EntityState.Detached;
            return null;
        }
    }

    /// <summary>Попытка доставки: успех отмечается в SentAt (без SaveChanges), сбой — только логируется.</summary>
    public async Task TrySendAsync(Notification notification, CancellationToken ct = default)
    {
        try
        {
            await sender.SendAsync(notification, ct);
            notification.SentAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Не удалось отправить оповещение {NotificationId} пользователю {UserId}",
                notification.Id, notification.UserId);
        }
    }
}
