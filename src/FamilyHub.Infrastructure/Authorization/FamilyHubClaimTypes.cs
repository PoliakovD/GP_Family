namespace FamilyHub.Infrastructure.Authorization;

/// <summary>Имена claim'ов, которые кладёт аутентификация (Telegram/Dev) в ClaimsPrincipal.</summary>
public static class FamilyHubClaimTypes
{
    /// <summary>Внутренний Guid пользователя (User.Id) — резолвится get-or-create по TelegramId.</summary>
    public const string UserId = "familyhub:user_id";

    /// <summary>Telegram user id, как пришёл из initData / dev-заголовка. У PWA-сессий отсутствует.</summary>
    public const string TelegramId = "familyhub:telegram_id";

    /// <summary>Провайдер аутентификации текущей сессии: "telegram" | "email" | "dev".</summary>
    public const string AuthProvider = "familyhub:auth_provider";

    /// <summary>Email пользователя — только у PWA-сессий (JWT access-токен).</summary>
    public const string Email = "familyhub:email";

    /// <summary>Id записи UserSession, которой выпущен текущий access-токен. Только PWA.</summary>
    public const string SessionId = "familyhub:session_id";
}
