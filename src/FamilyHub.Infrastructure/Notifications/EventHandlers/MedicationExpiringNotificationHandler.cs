using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Infrastructure.Notifications.EventHandlers;

/// <summary>
/// Фан-аут оповещения об истечении срока годности лекарства по активным членам семьи.
/// Форматы DedupKey (med-exp/med-expired) сохранены с до-событийной реализации
/// ReminderScanJob — идемпотентность непрерывна со старыми строками Notification.
/// </summary>
public class MedicationExpiringNotificationHandler(
    AppDbContext db,
    NotificationSendingService notifications) : INotificationHandler<MedicationExpiringEvent>
{
    public async Task Handle(MedicationExpiringEvent notification, CancellationToken cancellationToken)
    {
        var (type, dedupPrefix) = notification.IsExpired
            ? (NotificationType.MedicationExpired, "med-expired")
            : (NotificationType.MedicationExpiringSoon, "med-exp");

        var (title, body) = notification.IsExpired
            ? ($"Срок годности истёк: {notification.Name}",
               $"Лекарство «{notification.Name}» просрочено с {notification.ExpiryDate:dd.MM.yyyy}.")
            : ($"Истекает срок годности: {notification.Name}",
               $"Лекарство «{notification.Name}» истекает {notification.ExpiryDate:dd.MM.yyyy}.");

        var recipientIds = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.FamilyId == notification.FamilyId && m.Status == MemberStatus.Active)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);

        await notifications.NotifyAsync(
            recipientIds, notification.FamilyId, type, title, body,
            relatedEntityId: notification.MedicationId,
            dedupKeyFor: userId => $"{dedupPrefix}:{notification.MedicationId}:{userId}",
            ct: cancellationToken);
    }
}
