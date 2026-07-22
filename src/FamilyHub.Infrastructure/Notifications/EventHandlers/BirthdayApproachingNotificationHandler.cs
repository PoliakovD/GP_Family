using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Infrastructure.Notifications.EventHandlers;

/// <summary>
/// Фан-аут напоминания о приближающемся дне рождения по активным членам семьи.
/// Формат DedupKey (bday:{id}:{userId}:{year}) сохранён с до-событийной реализации.
/// </summary>
public class BirthdayApproachingNotificationHandler(
    AppDbContext db,
    NotificationSendingService notifications) : INotificationHandler<BirthdayApproachingEvent>
{
    public async Task Handle(BirthdayApproachingEvent notification, CancellationToken cancellationToken)
    {
        var title = $"Скоро день рождения: {notification.PersonName}";
        var body = notification.DaysUntil == 0
            ? $"У {notification.PersonName} день рождения сегодня!"
            : $"У {notification.PersonName} день рождения {notification.OccurrenceDate:dd.MM.yyyy} (через {notification.DaysUntil} дн.).";

        var recipientIds = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.FamilyId == notification.FamilyId && m.Status == MemberStatus.Active)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);

        await notifications.NotifyAsync(
            recipientIds, notification.FamilyId, NotificationType.BirthdayUpcoming, title, body,
            relatedEntityId: notification.BirthdayId,
            dedupKeyFor: userId => $"bday:{notification.BirthdayId}:{userId}:{notification.OccurrenceDate.Year}",
            ct: cancellationToken);
    }
}
