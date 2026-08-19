namespace FamilyHub.TelegramBot.Configuration;

/// <summary>
/// Секция "Telegram" — привязана к ТОЙ ЖЕ секции, что FamilyHub.Infrastructure.Telegram.TelegramOptions
/// в Api (env-переменные Telegram__* одинаковы в обоих контейнерах), но с другим набором ключей:
/// BotToken/WebhookSecret/WebhookUrl/MiniAppUrl принадлежат боту (веб-хук, SendMessage, WebApp-кнопки),
/// MaxInitDataAge/BotUsername — Api (Mini App auth, генерация deep-link). BotToken дублируется
/// намеренно: Api использует его для HMAC-ключа initData, а не для вызовов Bot API.
/// </summary>
public class BotOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = string.Empty;

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
