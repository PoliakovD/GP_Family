namespace FamilyHub.Infrastructure.Telegram;

/// <summary>
/// Конфигурация секции "Telegram" в appsettings — только то, что нужно FamilyHub.Api. После
/// выноса бота в FamilyHub.TelegramBot (см. BotOptions там) WebhookSecret/WebhookUrl/MiniAppUrl
/// уехали в тот процесс целиком: Api их больше не читает. BotToken остаётся ЗДЕСЬ ЖЕ (и
/// дублируется в конфиге бота) — TelegramInitDataValidator выводит из него HMAC-ключ для
/// проверки initData Mini App, это забота Api, а не бота.
/// </summary>
public class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = string.Empty;

    /// <summary>Максимальный возраст initData (auth_date), после которого она считается просроченной.</summary>
    public TimeSpan MaxInitDataAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Username бота без символа '@' (напр. "FamilyHubBot") — для формирования Deep Link инвайтов.</summary>
    public string BotUsername { get; set; } = string.Empty;
}
