using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.ValueObjects;
using FamilyHub.Infrastructure.Notifications;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Chat.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Modules.Chat.Chat;

/// <summary>
/// Уведомление о новом сообщении чата (ADR-0019 п. 7) — Hangfire-задача, чтобы отправка не задерживала ответ
/// на POST. Получатели — активные члены семьи, кроме автора и тех, кто прямо сейчас смотрит этот чат.
/// Не чаще одного на получателя за окно <see cref="ChatOptions.NotificationWindow"/>: DedupKey включает номер
/// окна, и NotificationSendingService просто не вставит второе. Текст — только имя автора и семья, без
/// содержимого; в Telegram/Web Push не уходит и это (обобщённый текст по типу — ADR-0004).
/// </summary>
public class ChatMessageNotifyJob(
    AppDbContext db,
    NotificationSendingService notifications,
    ChatPresenceTracker presence,
    IOptions<ChatOptions> options,
    ILogger<ChatMessageNotifyJob> logger)
{
    public async Task RunAsync(Guid messageId, CancellationToken ct)
    {
        var message = await db.ChatMessages.AsNoTracking()
            .Include(m => m.Author)
            .Include(m => m.Family)
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null || message.DeletedAt is not null)
        {
            logger.LogDebug("Чат: уведомление о {MessageId} не нужно — сообщения уже нет", messageId);
            return;
        }

        var familyId = message.FamilyId;
        var memberIds = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.FamilyId == familyId && m.Status == MemberStatus.Active && m.UserId != message.AuthorUserId)
            .Select(m => m.UserId)
            .ToListAsync(ct);
        var recipients = memberIds.Where(u => !presence.IsViewing(u, familyId)).ToList();
        if (recipients.Count == 0) return;

        var window = options.Value.NotificationWindow.Ticks;
        var bucket = message.CreatedAt.Ticks / Math.Max(window, 1);

        await notifications.NotifyAsync(
            recipients, familyId, NotificationType.FamilyChatMessage,
            "Сообщение в чате семьи",
            BuildBody(message.Author, message.Family.Name),
            familyId,
            userId => $"chat:{familyId:N}:{userId:N}:{bucket}",
            ct,
            NotificationRelatedKind.FamilyChat);

        logger.LogDebug(
            "Чат: уведомление о {MessageId} — получателей {Recipients} (смотрят чат: {Viewing})",
            messageId, recipients.Count, memberIds.Count - recipients.Count);
    }

    public static string BuildBody(User author, string familyName)
    {
        var name = PersonName.FormatOrDefault(
            author.LastName, author.FirstName, null, PersonNameStyle.ShortPatronymic, fallback: "Участник семьи");
        var verb = author.Gender switch
        {
            Gender.Female => "написала",
            Gender.Male => "написал",
            _ => "написал(а)",
        };
        return $"{name} {verb} в чат семьи «{familyName}»";
    }
}
