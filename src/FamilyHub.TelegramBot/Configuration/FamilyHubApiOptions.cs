namespace FamilyHub.TelegramBot.Configuration;

/// <summary>Секция "FamilyHubApi" — как боту достучаться до внутреннего API Api (/internal/bot/*).</summary>
public class FamilyHubApiOptions
{
    public const string SectionName = "FamilyHubApi";

    /// <summary>Базовый адрес Api внутри docker-сети, напр. "http://api:8080".</summary>
    public string BaseUrl { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 10;
}
