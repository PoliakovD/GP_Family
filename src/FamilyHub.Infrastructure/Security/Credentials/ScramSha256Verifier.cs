using System.Security.Cryptography;
using System.Text;

namespace FamilyHub.Infrastructure.Security.Credentials;

/// <summary>
/// Готовый SCRAM-SHA-256-верификатор пароля в формате, который Postgres хранит в pg_authid.rolpassword
/// (RFC 5802 / RFC 7677):
/// <code>SCRAM-SHA-256$&lt;итерации&gt;:&lt;соль base64&gt;$&lt;StoredKey base64&gt;:&lt;ServerKey base64&gt;</code>
///
/// Зачем: функции familyhub_admin.set_app_role_password принимают ТОЛЬКО верификатор, не пароль
/// (ADR-0011) — открытый пароль не должен попасть в лог сервера (при ошибке Postgres логирует текст
/// запроса) и в pg_stat_statements. Хэшируем на стороне приложения; сервер получает уже необратимое
/// значение, по которому может проверить пароль клиента, но не восстановить его.
/// </summary>
public static class ScramSha256Verifier
{
    /// <summary>Значение по умолчанию у Postgres (scram_iterations).</summary>
    public const int DefaultIterations = 4096;

    public static string Create(string password, byte[]? salt = null, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);

        // SASLprep не реализуем: для ASCII без управляющих символов он тождественен (наши пароли —
        // base64url, см. SecretGenerator). Не-ASCII молча нормализовать нельзя — хэш разошёлся бы с
        // тем, что вычислит сервер при входе, и роль осталась бы без рабочего пароля.
        if (!password.All(c => c is > ' ' and < (char)0x7F))
            throw new ArgumentException("Пароль должен состоять из печатных ASCII-символов без пробелов.", nameof(password));

        salt ??= RandomNumberGenerator.GetBytes(16);

        var saltedPassword = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        var clientKey = HMACSHA256.HashData(saltedPassword, "Client Key"u8);
        var storedKey = SHA256.HashData(clientKey);
        var serverKey = HMACSHA256.HashData(saltedPassword, "Server Key"u8);

        return $"SCRAM-SHA-256${iterations}:{Convert.ToBase64String(salt)}${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}";
    }
}
