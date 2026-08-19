namespace FamilyHub.TelegramBot.Configuration;

/// <summary>
/// Секция "Internal" — то же имя секции и тот же секрет, что у FamilyHub.Api.Configuration.
/// InternalOptions.BotApiToken: бот шлёт этот токен в заголовке X-Internal-Token
/// (см. Api/InternalTokenHandler), Api сверяет его constant-time сравнением.
/// </summary>
public class InternalApiOptions
{
    public const string SectionName = "Internal";

    public string BotApiToken { get; set; } = string.Empty;
}
