using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Notifications;

namespace FamilyHub.Modules.Chat.Chat;

/// <summary>Push о чате семьи (ADR-0019 п. 7): текст обобщённый (без имени автора и названия семьи —
/// ADR-0004), тег на семью — новое уведомление замещает предыдущее, а не копится стопкой; клик открывает
/// сам чат, а не список уведомлений.</summary>
public class ChatPushCustomizer : IPushPayloadCustomizer
{
    public bool CanHandle(NotificationType type) => type == NotificationType.FamilyChatMessage;

    public Task<PushCustomization?> BuildAsync(Notification notification, CancellationToken ct = default) =>
        Task.FromResult<PushCustomization?>(new PushCustomization(
            Body: "Новое сообщение в чате семьи",
            Tag: $"chat-{notification.RelatedEntityId:N}",
            Renotify: true,
            DefaultUrl: $"/families/{notification.RelatedEntityId}?tab=chat"));
}
