using System.Security.Cryptography;

namespace FamilyHub.Infrastructure.Security;

/// <summary>
/// Генератор временного пароля для аккаунта, созданного через привязку Telegram (см.
/// TelegramBindingService) — отправляется на email, чтобы у нового (или "осиротевшего" без
/// пароля) аккаунта сразу появился рабочий вход в PWA; без этого PwaAuthService.LoginAsync
/// (фильтрует PasswordHash != null) никогда не смог бы его аутентифицировать иначе как через
/// Telegram. Валиден ПО ПОСТРОЕНИЮ — гарантированно проходит
/// FamilyHub.Domain.ValueObjects.PasswordRules.IsValid, а не generate-and-retry (см.
/// TemporaryPasswordGeneratorTests). Символы подобраны так, чтобы человек мог надёжно
/// перепечатать пароль из письма — без визуально неоднозначных символов (0/O, 1/l/I).
/// </summary>
public static class TemporaryPasswordGenerator
{
    private const int Length = 12;
    private const string Lower = "abcdefghijkmnpqrstuvwxyz";
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Digits = "23456789";
    private const string All = Lower + Upper + Digits;

    public static string Generate()
    {
        Span<char> chars = stackalloc char[Length];

        // Гарантируем по одному символу из каждого обязательного класса ДО заполнения
        // остального и перемешивания — так результат проходит PasswordRules.IsValid при любом
        // исходе перемешивания, без повторных попыток.
        chars[0] = Lower[RandomNumberGenerator.GetInt32(Lower.Length)];
        chars[1] = Upper[RandomNumberGenerator.GetInt32(Upper.Length)];
        chars[2] = Digits[RandomNumberGenerator.GetInt32(Digits.Length)];
        for (var i = 3; i < Length; i++)
            chars[i] = All[RandomNumberGenerator.GetInt32(All.Length)];

        RandomNumberGenerator.Shuffle(chars);
        return new string(chars);
    }
}
