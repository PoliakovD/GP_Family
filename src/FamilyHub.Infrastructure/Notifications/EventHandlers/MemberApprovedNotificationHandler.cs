using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Infrastructure.Notifications.EventHandlers;

/// <summary>
/// Оповещение членов семьи о новом участнике (заявка одобрена админом).
/// Сам новичок оповещение не получает.
/// </summary>
public class MemberApprovedNotificationHandler(
    AppDbContext db,
    NotificationSendingService notifications) : INotificationHandler<MemberApprovedEvent>
{
    public async Task Handle(MemberApprovedEvent notification, CancellationToken cancellationToken)
    {
        var familyName = await db.Families.AsNoTracking()
            .Where(f => f.Id == notification.FamilyId)
            .Select(f => f.Name)
            .FirstOrDefaultAsync(cancellationToken);
        if (familyName is null) return;

        var userName = await db.Users.AsNoTracking()
            .Where(u => u.Id == notification.UserId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Новый участник";

        var recipientIds = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.FamilyId == notification.FamilyId
                && m.Status == MemberStatus.Active && m.UserId != notification.UserId)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);

        await notifications.NotifyAsync(
            recipientIds, notification.FamilyId, NotificationType.MemberApproved,
            $"Новый участник семьи: {userName}",
            $"{userName} теперь состоит в семье «{familyName}».",
            relatedEntityId: notification.UserId,
            dedupKeyFor: userId => $"member-approved:{notification.EventId}:{userId}",
            ct: cancellationToken);
    }
}
