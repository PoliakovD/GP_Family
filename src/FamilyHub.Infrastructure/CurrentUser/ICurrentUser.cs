namespace FamilyHub.Infrastructure.CurrentUser;

/// <summary>
/// Текущий аутентифицированный пользователь запроса. UserId резолвится
/// get-or-create'ом по TelegramId в момент аутентификации (см. TelegramAuthenticationHandler
/// и DevAuthenticationHandler) — здесь только чтение готовых claim'ов.
/// </summary>
public interface ICurrentUser
{
    Guid UserId { get; }

    long TelegramId { get; }
}
