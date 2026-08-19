namespace FamilyHub.Api.Configuration;

/// <summary>
/// Секция "Internal" — секреты для межпроцессного вызова FamilyHub.TelegramBot → FamilyHub.Api
/// (см. InternalBotEndpoints/InternalBotAuthFilter). Не пересекается с публичной аутентификацией
/// (JWT/Telegram initData) — это отдельный периметр для одного доверенного клиента, никогда не
/// проксируемый через Caddy наружу (см. deploy/Caddyfile, /internal/* в @blocked).
/// </summary>
public class InternalOptions
{
    public const string SectionName = "Internal";

    /// <summary>
    /// Секрет в заголовке X-Internal-Token, которым FamilyHub.TelegramBot подтверждает себя.
    /// Сравнение — constant-time (см. InternalBotAuthFilter), тот же принцип, что у
    /// BotEndpoints.IsValidSecret в самом боте.
    /// </summary>
    public string BotApiToken { get; set; } = string.Empty;
}
