using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Chat.Realtime;

/// <summary>Что произошло с сообщением — клиент в любом случае перечитывает его по id.</summary>
public enum ChatChange { Created, Edited, Deleted, Reactions }

/// <summary>Событие хаба: только идентификаторы, без содержимого (ADR-0019 п. 4).</summary>
public record ChatMessageChangedEvent(Guid FamilyId, Guid MessageId, ChatChange Change);

/// <summary>Рассылка событий чата — отдельная абстракция, чтобы ChatService тестировался без SignalR.</summary>
public interface IChatEvents
{
    Task MessageChangedAsync(Guid familyId, Guid messageId, ChatChange change, CancellationToken ct = default);
}

public class ChatEvents(IHubContext<ChatHub> hub, ILogger<ChatEvents> logger) : IChatEvents
{
    public const string MessageChangedMethod = "messageChanged";

    /// <summary>Сбой рассылки не должен откатывать уже сохранённое сообщение — клиенты дочитают при
    /// переподключении/открытии чата.</summary>
    public async Task MessageChangedAsync(Guid familyId, Guid messageId, ChatChange change, CancellationToken ct = default)
    {
        try
        {
            await hub.Clients.Group(ChatHub.GroupName(familyId))
                .SendAsync(MessageChangedMethod, new ChatMessageChangedEvent(familyId, messageId, change), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Чат: не удалось разослать событие {Change} по сообщению {MessageId}", change, messageId);
        }
    }
}
