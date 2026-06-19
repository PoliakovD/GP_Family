namespace FamilyHub.Infrastructure.Authorization;

/// <summary>Имена claim'ов, которые кладёт аутентификация (Telegram/Dev) в ClaimsPrincipal.</summary>
public static class FamilyHubClaimTypes
{
    /// <summary>Внутренний Guid пользователя (User.Id) — резолвится get-or-create по TelegramId.</summary>
    public const string UserId = "familyhub:user_id";

    /// <summary>Telegram user id, как пришёл из initData / dev-заголовка.</summary>
    public const string TelegramId = "familyhub:telegram_id";
}
