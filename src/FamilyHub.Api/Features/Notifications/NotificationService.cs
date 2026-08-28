using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Api.Features.Notifications;

public record NotificationDto(
    Guid Id,
    NotificationType Type,
    string Title,
    string Body,
    Guid RelatedEntityId,
    DateTime CreatedAt,
    bool IsRead,
    DateTime? ReadAt);

public enum MarkReadResult { Success, NotFound }

public class NotificationService(AppDbContext db, ILogger<NotificationService> logger)
{
    /// <summary>Только свои оповещения, новые сверху (раздел "доступ строго по UserId").</summary>
    public async Task<List<NotificationDto>> GetMyNotificationsAsync(Guid userId, bool unreadOnly, CancellationToken ct = default)
    {
        var query = db.Notifications.AsNoTracking().Where(n => n.UserId == userId);
        if (unreadOnly)
            query = query.Where(n => !n.IsRead);

        var result = await query
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => new NotificationDto(n.Id, n.Type, n.Title, n.Body, n.RelatedEntityId, n.CreatedAt, n.IsRead, n.ReadAt))
            .ToListAsync(ct);

        logger.LogDebug(
            "Загружено {Count} оповещений пользователя {UserId} (unreadOnly={UnreadOnly})", result.Count, userId, unreadOnly);
        return result;
    }

    /// <summary>Только счётчик — редизайн v2, бейдж сайдбара/таба «Ещё». Отдельно от
    /// GetMyNotificationsAsync: тот тянет и расшифровывает полные тела ради одного числа, здесь —
    /// один COUNT-запрос без выборки строк.</summary>
    public Task<int> GetUnreadCountAsync(Guid userId, CancellationToken ct = default) =>
        db.Notifications.AsNoTracking().CountAsync(n => n.UserId == userId && !n.IsRead, ct);

    /// <summary>Отметить прочитанным — только если оповещение принадлежит текущему пользователю.</summary>
    public async Task<MarkReadResult> MarkReadAsync(Guid notificationId, Guid userId, CancellationToken ct = default)
    {
        var notification = await db.Notifications.FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == userId, ct);
        if (notification is null)
        {
            logger.LogWarning(
                "Отметка о прочтении: оповещение {NotificationId} не найдено у пользователя {UserId}", notificationId, userId);
            return MarkReadResult.NotFound;
        }

        if (!notification.IsRead)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogDebug("Оповещение {NotificationId} отмечено прочитанным пользователем {UserId}", notificationId, userId);
        }

        return MarkReadResult.Success;
    }
}
