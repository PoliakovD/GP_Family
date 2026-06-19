using FamilyHub.Infrastructure.Authorization;
using Microsoft.AspNetCore.Http;

namespace FamilyHub.Infrastructure.CurrentUser;

public class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid UserId => accessor.HttpContext?.User.GetUserId()
        ?? throw new InvalidOperationException("Запрос не аутентифицирован — UserId недоступен.");

    public long TelegramId => accessor.HttpContext?.User.GetTelegramId()
        ?? throw new InvalidOperationException("Запрос не аутентифицирован — TelegramId недоступен.");
}
