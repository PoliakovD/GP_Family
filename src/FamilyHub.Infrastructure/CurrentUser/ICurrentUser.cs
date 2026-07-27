namespace FamilyHub.Infrastructure.CurrentUser;

/// <summary>
/// Текущий аутентифицированный пользователь запроса. UserId резолвится в момент
/// аутентификации (Telegram-хендлеры — get-or-create по TelegramId; PWA-cookie — из клейма
/// сессии) — здесь только чтение готовых claim'ов.
/// </summary>
public interface ICurrentUser
{
    Guid UserId { get; }

    /// <summary>null — сессия PWA (email/пароль), Telegram-клейма нет.</summary>
    long? TelegramId { get; }
}
