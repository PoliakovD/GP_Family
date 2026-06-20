namespace FamilyHub.Infrastructure.Telegram;

/// <summary>Конфигурация секции "Telegram" в appsettings.</summary>
public class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = string.Empty;

    /// <summary>Максимальный возраст initData (auth_date), после которого она считается просроченной.</summary>
    public TimeSpan MaxInitDataAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Секрет, который Telegram присылает в заголовке X-Telegram-Bot-Api-Secret-Token при каждом
    /// вызове вебхука (передаётся в setWebhook). Проверяется ПЕРВЫМ, до обработки апдейта.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Публичный HTTPS-URL вебхука бота (используется при регистрации через setWebhook).</summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>URL Telegram Mini App — для кнопок "Открыть FamilyHub" и menu-button бота.</summary>
    public string MiniAppUrl { get; set; } = string.Empty;
}
