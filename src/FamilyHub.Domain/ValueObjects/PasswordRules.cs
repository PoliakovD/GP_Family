namespace FamilyHub.Domain.ValueObjects;

/// <summary>
/// Правила пароля PWA-входа (email + пароль; заменил собой 4-8-значный numeric PIN).
/// Единый источник истины для бэкенда — фронтенд дублирует тот же паттерн для UX-валидации,
/// но финальный арбитр всегда сервер. Только формат создания/смены пароля — вход
/// (PwaAuthService.LoginAsync) НЕ проверяет это правило, иначе учётки с уже установленным
/// (в том числе старым, ещё PIN-формата) паролем потеряли бы возможность входа.
/// </summary>
public static class PasswordRules
{
    public const int MinLength = 8;

    /// <summary>DoS-guard на вход PBKDF2, а не ограничение хранения — сам хеш занимает ~83 символа
    /// независимо от длины исходного пароля.</summary>
    public const int MaxLength = 100;

    /// <summary>Строчная + заглавная латинские буквы + цифра, длина 8-100. Без требований к
    /// спецсимволам — не запрашивалось.</summary>
    public static bool IsValid(string password) =>
        password.Length is >= MinLength and <= MaxLength
        && password.Any(char.IsAsciiLetterLower)
        && password.Any(char.IsAsciiLetterUpper)
        && password.Any(char.IsAsciiDigit);
}
