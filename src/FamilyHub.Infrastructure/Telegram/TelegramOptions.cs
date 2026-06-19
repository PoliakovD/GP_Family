namespace FamilyHub.Infrastructure.Telegram;

/// <summary>Конфигурация секции "Telegram" в appsettings.</summary>
public class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = string.Empty;

    /// <summary>Максимальный возраст initData (auth_date), после которого она считается просроченной.</summary>
    public TimeSpan MaxInitDataAge { get; set; } = TimeSpan.FromHours(24);
}
