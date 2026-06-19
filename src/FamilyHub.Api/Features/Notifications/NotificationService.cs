using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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

public class NotificationService(AppDbContext db)
{
    /// <summary>Только свои оповещения, новые сверху (раздел "доступ строго по UserId").</summary>
    public Task<List<NotificationDto>> GetMyNotificationsAsync(Guid userId, bool unreadOnly, CancellationToken ct = default)
    {
        var query = db.Notifications.AsNoTracking().Where(n => n.UserId == userId);
        if (unreadOnly)
            query = query.Where(n => !n.IsRead);

        return query
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => new NotificationDto(n.Id, n.Type, n.Title, n.Body, n.RelatedEntityId, n.CreatedAt, n.IsRead, n.ReadAt))
            .ToListAsync(ct);
    }

    /// <summary>Отметить прочитанным — только если оповещение принадлежит текущему пользователю.</summary>
    public async Task<MarkReadResult> MarkReadAsync(Guid notificationId, Guid userId, CancellationToken ct = default)
    {
        var notification = await db.Notifications.FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == userId, ct);
        if (notification is null) return MarkReadResult.NotFound;

        if (!notification.IsRead)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return MarkReadResult.Success;
    }
}
