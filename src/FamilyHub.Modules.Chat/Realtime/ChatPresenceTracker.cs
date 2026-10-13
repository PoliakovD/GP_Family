using System.Collections.Concurrent;

namespace FamilyHub.Modules.Chat.Realtime;

/// <summary>
/// Кто прямо сейчас смотрит чат какой семьи — по соединениям хаба (вкладка с открытым чатом и видимой
/// страницей шлёт Viewing, при уходе/скрытии — StopViewing, разрыв соединения чистит сам). Им не нужно
/// уведомление о новом сообщении: оно уже на экране (ADR-0019 п. 7). In-memory — как и сам хаб без
/// бэкплейна: верно, пока реплика API одна.
/// </summary>
public class ChatPresenceTracker
{
    private readonly ConcurrentDictionary<string, (Guid UserId, Guid FamilyId)> viewing = new();

    public void SetViewing(string connectionId, Guid userId, Guid familyId) =>
        viewing[connectionId] = (userId, familyId);

    public void Clear(string connectionId) => viewing.TryRemove(connectionId, out _);

    public bool IsViewing(Guid userId, Guid familyId) =>
        viewing.Values.Any(v => v.UserId == userId && v.FamilyId == familyId);
}
