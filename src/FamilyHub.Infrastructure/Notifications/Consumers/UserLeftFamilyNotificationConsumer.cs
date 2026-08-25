using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.ValueObjects;
using FamilyHub.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Infrastructure.Notifications.Consumers;

/// <summary>
/// TG-алерт админам семьи о том, что участник покинул семью (сам или выгнан).
/// Идемпотентность повторной доставки — DedupKey с MessageId события (устойчив к редоставке
/// в пределах ретраев одного этого потребителя — см. ADR-0006 про отказ от общего ретрая строки).
/// </summary>
public class UserLeftFamilyNotificationConsumer(
    AppDbContext db,
    NotificationSendingService notifications) : IConsumer<UserLeftFamilyEvent>
{
    public async Task Consume(ConsumeContext<UserLeftFamilyEvent> context)
    {
        var notification = context.Message;
        var ct = context.CancellationToken;

        // Семьи может уже не быть (каскад при удалении семьи) — тогда оповещать некого.
        var familyName = await db.Families.AsNoTracking()
            .Where(f => f.Id == notification.FamilyId)
            .Select(f => f.Name)
            .FirstOrDefaultAsync(ct);
        if (familyName is null) return;

        // Запись User переживает выход из семьи — имя доступно.
        var leftUser = await db.Users.AsNoTracking()
            .Where(u => u.Id == notification.UserId)
            .Select(u => new { u.LastName, u.FirstName, u.MiddleName })
            .FirstOrDefaultAsync(ct);
        var userName = leftUser is null
            ? "Участник"
            : PersonName.FormatOrDefault(leftUser.LastName, leftUser.FirstName, leftUser.MiddleName, PersonNameStyle.Full, "Участник");

        var adminIds = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.FamilyId == notification.FamilyId
                && m.Role == FamilyRole.Admin && m.Status == MemberStatus.Active)
            .Select(m => m.UserId)
            .ToListAsync(ct);

        await notifications.NotifyAsync(
            adminIds, notification.FamilyId, NotificationType.MemberLeft,
            $"Участник покинул семью: {userName}",
            $"{userName} больше не состоит в семье «{familyName}». Доступ к его медицинским данным отозван.",
            relatedEntityId: notification.UserId,
            dedupKeyFor: userId => $"member-left:{context.MessageId}:{userId}",
            ct: ct);
    }
}
