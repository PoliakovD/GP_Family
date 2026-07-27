using System.Security.Cryptography;

namespace FamilyHub.Infrastructure.Security;

/// <summary>
/// PBKDF2-хеширование пароля PWA-входа. Формат: "pbkdf2:{iterations}:{saltB64}:{hashB64}"
/// (итерации в строке — переживает будущее повышение стоимости без перехеширования всех).
/// Выбран PBKDF2, а не Argon2: без внешних пакетов; политика пароля (см.
/// FamilyHub.Domain.ValueObjects.PasswordRules — 8+ симв., строчная+заглавная+цифра) и
/// lockout/rate limiting — основная защита от брутфорса, не сама стоимость хеша (см.
/// threat-model.md). Формат/итерации не менялись при переходе с PIN на пароль — старые
/// PIN-хеши (4-8 цифр) продолжают верифицироваться этой же функцией без миграции данных.
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"pbkdf2:{Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split(':');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iterations))
            return false;

        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
