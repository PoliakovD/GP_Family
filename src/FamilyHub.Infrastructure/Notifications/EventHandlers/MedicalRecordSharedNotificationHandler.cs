using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Infrastructure.Notifications.EventHandlers;

/// <summary>
/// Оповещение членов семьи о том, что участник открыл им доступ к своим медицинским
/// записям (FamilyMedicalShare создан). Сам владелец оповещение не получает.
/// </summary>
public class MedicalRecordSharedNotificationHandler(
    AppDbContext db,
    NotificationSendingService notifications) : INotificationHandler<MedicalRecordSharedEvent>
{
    public async Task Handle(MedicalRecordSharedEvent notification, CancellationToken cancellationToken)
    {
        var familyName = await db.Families.AsNoTracking()
            .Where(f => f.Id == notification.FamilyId)
            .Select(f => f.Name)
            .FirstOrDefaultAsync(cancellationToken);
        if (familyName is null) return;

        var ownerName = await db.Users.AsNoTracking()
            .Where(u => u.Id == notification.OwnerUserId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Участник";

        var recipientIds = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.FamilyId == notification.FamilyId
                && m.Status == MemberStatus.Active && m.UserId != notification.OwnerUserId)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);

        await notifications.NotifyAsync(
            recipientIds, notification.FamilyId, NotificationType.MedicalRecordShared,
            $"Открыт доступ к мед-записям: {ownerName}",
            $"{ownerName} открыл(а) семье «{familyName}» доступ к своим медицинским записям.",
            relatedEntityId: notification.OwnerUserId,
            dedupKeyFor: userId => $"record-shared:{notification.EventId}:{userId}",
            ct: cancellationToken);
    }
}
