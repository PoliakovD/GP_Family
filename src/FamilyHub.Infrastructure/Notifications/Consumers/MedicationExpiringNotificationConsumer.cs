using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Infrastructure.Notifications.Consumers;

/// <summary>
/// Фан-аут оповещения об истечении срока годности лекарства по активным членам семьи.
/// Форматы DedupKey (med-exp/med-expired) сохранены с до-событийной реализации
/// ReminderScanJob — идемпотентность непрерывна со старыми строками Notification; собраны из
/// MedicationId, а не MessageId, специально, чтобы не зависеть от смены транспорта.
/// </summary>
public class MedicationExpiringNotificationConsumer(
    AppDbContext db,
    NotificationSendingService notifications) : IConsumer<MedicationExpiringEvent>
{
    public async Task Consume(ConsumeContext<MedicationExpiringEvent> context)
    {
        var notification = context.Message;
        var ct = context.CancellationToken;

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
            .ToListAsync(ct);

        await notifications.NotifyAsync(
            recipientIds, notification.FamilyId, type, title, body,
            relatedEntityId: notification.MedicationId,
            dedupKeyFor: userId => $"{dedupPrefix}:{notification.MedicationId}:{userId}",
            ct: ct);
    }
}
