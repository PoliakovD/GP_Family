using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Infrastructure.Notifications.Consumers;

/// <summary>
/// Оповещение членов семьи о том, что участник открыл им доступ к своим медицинским
/// записям (FamilyMedicalShare создан). Сам владелец оповещение не получает.
/// </summary>
public class MedicalRecordSharedNotificationConsumer(
    AppDbContext db,
    NotificationSendingService notifications) : IConsumer<MedicalRecordSharedEvent>
{
    public async Task Consume(ConsumeContext<MedicalRecordSharedEvent> context)
    {
        var notification = context.Message;
        var ct = context.CancellationToken;

        var familyName = await db.Families.AsNoTracking()
            .Where(f => f.Id == notification.FamilyId)
            .Select(f => f.Name)
            .FirstOrDefaultAsync(ct);
        if (familyName is null) return;

        var ownerName = await db.Users.AsNoTracking()
            .Where(u => u.Id == notification.OwnerUserId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct) ?? "Участник";

        var recipientIds = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.FamilyId == notification.FamilyId
                && m.Status == MemberStatus.Active && m.UserId != notification.OwnerUserId)
            .Select(m => m.UserId)
            .ToListAsync(ct);

        await notifications.NotifyAsync(
            recipientIds, notification.FamilyId, NotificationType.MedicalRecordShared,
            $"Открыт доступ к мед-записям: {ownerName}",
            $"{ownerName} открыл(а) семье «{familyName}» доступ к своим медицинским записям.",
            relatedEntityId: notification.OwnerUserId,
            dedupKeyFor: userId => $"record-shared:{context.MessageId}:{userId}",
            ct: ct);
    }
}
