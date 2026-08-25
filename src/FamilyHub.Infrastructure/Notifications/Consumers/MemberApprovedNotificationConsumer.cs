using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.ValueObjects;
using FamilyHub.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Infrastructure.Notifications.Consumers;

/// <summary>
/// Оповещение членов семьи о новом участнике (заявка одобрена админом).
/// Сам новичок оповещение не получает.
/// </summary>
public class MemberApprovedNotificationConsumer(
    AppDbContext db,
    NotificationSendingService notifications) : IConsumer<MemberApprovedEvent>
{
    public async Task Consume(ConsumeContext<MemberApprovedEvent> context)
    {
        var notification = context.Message;
        var ct = context.CancellationToken;

        var familyName = await db.Families.AsNoTracking()
            .Where(f => f.Id == notification.FamilyId)
            .Select(f => f.Name)
            .FirstOrDefaultAsync(ct);
        if (familyName is null) return;

        var newMember = await db.Users.AsNoTracking()
            .Where(u => u.Id == notification.UserId)
            .Select(u => new { u.LastName, u.FirstName, u.MiddleName })
            .FirstOrDefaultAsync(ct);
        var userName = newMember is null
            ? "Новый участник"
            : PersonName.FormatOrDefault(newMember.LastName, newMember.FirstName, newMember.MiddleName, PersonNameStyle.Full, "Новый участник");

        var recipientIds = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.FamilyId == notification.FamilyId
                && m.Status == MemberStatus.Active && m.UserId != notification.UserId)
            .Select(m => m.UserId)
            .ToListAsync(ct);

        await notifications.NotifyAsync(
            recipientIds, notification.FamilyId, NotificationType.MemberApproved,
            $"Новый участник семьи: {userName}",
            $"{userName} теперь состоит в семье «{familyName}».",
            relatedEntityId: notification.UserId,
            dedupKeyFor: userId => $"member-approved:{context.MessageId}:{userId}",
            ct: ct);
    }
}
