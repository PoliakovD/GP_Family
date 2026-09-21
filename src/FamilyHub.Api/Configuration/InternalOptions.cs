using Microsoft.Extensions.Options;

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
    /// BotEndpoints.IsValidSecret в самом боте. Пусто — легитимно (бот не подключён, обычно
    /// локальная разработка без контейнера бота); заполнено — обязано быть ≥32 символов, см.
    /// InternalOptionsValidator.
    /// </summary>
    public string BotApiToken { get; set; } = string.Empty;
}

/// <summary>
/// Fail-fast при старте хоста (cleanup-рефакторинг — заменяет прежнюю ручную проверку сырой
/// строки конфига в AddFamilyHubNotificationChannels). BotApiToken опционален (см. class doc) —
/// проверяется только МИНИМАЛЬНАЯ длина, когда он задан, не сам факт задания.
/// </summary>
public class InternalOptionsValidator : IValidateOptions<InternalOptions>
{
    public ValidateOptionsResult Validate(string? name, InternalOptions options) =>
        !string.IsNullOrWhiteSpace(options.BotApiToken) && options.BotApiToken.Length < 32
            ? ValidateOptionsResult.Fail(
                "Internal:BotApiToken (env Internal__BotApiToken) короче 32 символов — секрет обмена с " +
                "FamilyHub.TelegramBot слишком слабый. Сгенерировать: `openssl rand -hex 32`.")
            : ValidateOptionsResult.Success;
}
