using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Infrastructure.Notifications.EventHandlers;

/// <summary>
/// TG-алерт админам семьи о том, что участник покинул семью (сам или выгнан).
/// Идемпотентность повторной доставки — DedupKey с EventId события.
/// </summary>
public class UserLeftFamilyNotificationHandler(
    AppDbContext db,
    NotificationSendingService notifications) : INotificationHandler<UserLeftFamilyEvent>
{
    public async Task Handle(UserLeftFamilyEvent notification, CancellationToken cancellationToken)
    {
        // Семьи может уже не быть (каскад при удалении семьи) — тогда оповещать некого.
        var familyName = await db.Families.AsNoTracking()
            .Where(f => f.Id == notification.FamilyId)
            .Select(f => f.Name)
            .FirstOrDefaultAsync(cancellationToken);
        if (familyName is null) return;

        // Запись User переживает выход из семьи — имя доступно.
        var userName = await db.Users.AsNoTracking()
            .Where(u => u.Id == notification.UserId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Участник";

        var adminIds = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.FamilyId == notification.FamilyId
                && m.Role == FamilyRole.Admin && m.Status == MemberStatus.Active)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);

        await notifications.NotifyAsync(
            adminIds, notification.FamilyId, NotificationType.MemberLeft,
            $"Участник покинул семью: {userName}",
            $"{userName} больше не состоит в семье «{familyName}». Доступ к его медицинским данным отозван.",
            relatedEntityId: notification.UserId,
            dedupKeyFor: userId => $"member-left:{notification.EventId}:{userId}",
            ct: cancellationToken);
    }
}
