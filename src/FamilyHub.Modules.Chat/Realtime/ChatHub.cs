using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Auth;
using FamilyHub.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Chat.Realtime;

/// <summary>
/// Хаб чата семьи (ADR-0019). Вход — только по короткоживущему тикету (схема HubTicket, п. 3). Клиент
/// ничего через хаб не отправляет — сообщения идут обычным REST (с CSRF, лимитами и консент-фильтром), хаб
/// лишь раздаёт события <see cref="ChatEvents.MessageChangedMethod"/> и принимает сигнал «смотрю чат».
/// </summary>
[Authorize(AuthenticationSchemes = AuthSchemes.HubTicket)]
public class ChatHub(
    IFamilyAccessService access,
    ChatPresenceTracker presence,
    ILogger<ChatHub> logger) : Hub
{
    public static string GroupName(Guid familyId) => $"family:{familyId:N}";

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User!.GetUserId()!.Value;
        var familyIds = await access.GetActiveFamilyIdsAsync(userId, Context.ConnectionAborted);
        foreach (var familyId in familyIds)
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(familyId), Context.ConnectionAborted);

        logger.LogDebug(
            "Чат: соединение {ConnectionId} пользователя {UserId} подписано на {Count} семей",
            Context.ConnectionId, userId, familyIds.Count);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        presence.Clear(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Открыт (и виден) чат семьи. Заодно подписывает на группу — на случай, если в семью вступили
    /// уже после подключения.</summary>
    public async Task Viewing(Guid familyId)
    {
        var userId = Context.User!.GetUserId()!.Value;
        if (!await access.HasRoleAsync(userId, familyId, FamilyRole.Member, Context.ConnectionAborted))
            throw new HubException("Нет доступа к чату этой семьи.");

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(familyId), Context.ConnectionAborted);
        presence.SetViewing(Context.ConnectionId, userId, familyId);
    }

    public void StopViewing() => presence.Clear(Context.ConnectionId);
}
