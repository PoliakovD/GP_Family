using System.Text.RegularExpressions;

namespace FamilyHub.Domain.ValueObjects;

/// <summary>
/// Правила видимого username веб-аккаунта (в отличие от User.TgUsername — зеркала
/// Telegram-хэндла). Формат как в Telegram: 5-32 симв., латиница/цифры/'_', с буквы.
/// Единый источник истины для бэкенда — фронтенд дублирует тот же паттерн для UX-валидации,
/// но финальный арбитр всегда сервер (формат + уникальный индекс).
/// </summary>
public static partial class UsernameRules
{
    public const string Pattern = "^[a-z][a-z0-9_]{4,31}$";

    [GeneratedRegex(Pattern)]
    private static partial Regex UsernameRegex();

    /// <summary>Trim + lowercase — применять до валидации и перед сравнением/сохранением.</summary>
    public static string Normalize(string username) => username.Trim().ToLowerInvariant();

    /// <summary>Ожидает уже нормализованную строку (см. <see cref="Normalize"/>).</summary>
    public static bool IsValid(string normalizedUsername) => UsernameRegex().IsMatch(normalizedUsername);
}
