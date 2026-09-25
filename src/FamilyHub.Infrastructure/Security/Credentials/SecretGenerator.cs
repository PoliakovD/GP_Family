using System.Security.Cryptography;

namespace FamilyHub.Infrastructure.Security.Credentials;

/// <summary>
/// Генерация секретов учёток приложения (ADR-0011) из криптостойкого источника. Наборы символов
/// подобраны так, чтобы значение без экранирования жило и в строке подключения Npgsql (нет ';', '=',
/// кавычек), и в docker compose (нет '$'), и в .env (нет пробелов/'#'), и в MinIO (длины/алфавит по
/// его правилам: access key 3..20, secret key 8..40 символов).
/// </summary>
public static class SecretGenerator
{
    private const string Base64UrlAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    private const string UpperAlphanumeric = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const string Alphanumeric = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>Префикс access key service account приложения: по нему сразу видно, чей это ключ
    /// (тот же, что выпускает deploy/scripts/bootstrap-app-credentials.sh).</summary>
    public const string MinioAccessKeyPrefix = "FHAPP";

    /// <summary>Пароль роли Postgres: 43 символа base64url (≈258 бит).</summary>
    public static string PostgresPassword() => Pick(Base64UrlAlphabet, 43);

    /// <summary>Access key MinIO: «FHAPP» + 15 символов A-Z0-9 (всего 20 — максимум у MinIO).</summary>
    public static string MinioAccessKey() => MinioAccessKeyPrefix + Pick(UpperAlphanumeric, 15);

    /// <summary>Secret key MinIO: 40 символов A-Za-z0-9 (максимум у MinIO).</summary>
    public static string MinioSecretKey() => Pick(Alphanumeric, 40);

    // GetItems выбирает равномерно (без смещения по модулю), в отличие от наивного `byte % n`.
    private static string Pick(string alphabet, int length) =>
        new(RandomNumberGenerator.GetItems<char>(alphabet, length));
}
