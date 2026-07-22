using System.Security.Cryptography;

namespace FamilyHub.Infrastructure.Security;

/// <summary>
/// PBKDF2-хеширование PIN-кода PWA-входа. Формат: "pbkdf2:{iterations}:{saltB64}:{hashB64}"
/// (итерации в строке — переживает будущее повышение стоимости без перехеширования всех).
/// Выбран PBKDF2, а не Argon2: без внешних пакетов; низкую энтропию PIN компенсируют
/// lockout и rate limiting, а не стоимость хеша (см. threat-model.md).
/// </summary>
public static class PinHasher
{
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"pbkdf2:{Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string pin, string stored)
    {
        var parts = stored.Split(':');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iterations))
            return false;

        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
