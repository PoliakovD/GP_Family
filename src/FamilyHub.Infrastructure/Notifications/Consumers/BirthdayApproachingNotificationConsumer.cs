using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Infrastructure.Notifications.Consumers;

/// <summary>
/// Фан-аут напоминания о приближающемся дне рождения по активным членам семьи. DedupKey
/// (identity rework) получил FamilyId и префикс источника —
/// bday:{SubjectKind}:{SubjectId}:{FamilyId}:{userId}:{year}. FamilyId обязателен: DedupKey
/// уникален ГЛОБАЛЬНО (NotificationSendingService.AddIfNewAsync проверяет только сам DedupKey,
/// не пару с FamilyId), а SubjectId у Member-события — это UserId именинника, который теперь
/// легитимно повторяется в разных семьях (человек в нескольких семьях). Без FamilyId в ключе
/// оповещение по второй семье того же именинника тому же получателю тихо отбрасывалось бы как
/// "дубликат" первого — ровно так и произошло при первой реализации этого фан-аута.
/// </summary>
public class BirthdayApproachingNotificationConsumer(
    AppDbContext db,
    NotificationSendingService notifications) : IConsumer<BirthdayApproachingEvent>
{
    public async Task Consume(ConsumeContext<BirthdayApproachingEvent> context)
    {
        var notification = context.Message;
        var ct = context.CancellationToken;

        var title = $"Скоро день рождения: {notification.PersonName}";
        var body = notification.DaysUntil == 0
            ? $"У {notification.PersonName} день рождения сегодня!"
            : $"У {notification.PersonName} день рождения {notification.OccurrenceDate:dd.MM.yyyy} (через {notification.DaysUntil} дн.).";

        // Сам именинник (SubjectKind.Member) оповещение о своём же ДР не получает.
        var recipientIds = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.FamilyId == notification.FamilyId && m.Status == MemberStatus.Active
                && m.UserId != notification.SubjectUserId)
            .Select(m => m.UserId)
            .ToListAsync(ct);

        await notifications.NotifyAsync(
            recipientIds, notification.FamilyId, NotificationType.BirthdayUpcoming, title, body,
            relatedEntityId: notification.SubjectId,
            dedupKeyFor: userId =>
                $"bday:{notification.SubjectKind}:{notification.SubjectId}:{notification.FamilyId}:{userId}:{notification.OccurrenceDate.Year}",
            ct: ct);
    }
}
