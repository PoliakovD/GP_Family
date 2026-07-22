using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Storage;

/// <summary>Настройки выдачи ссылок на скачивание вложений (секция "Attachments").</summary>
public class AttachmentDownloadOptions
{
    public const string SectionName = "Attachments";

    /// <summary>Секрет HMAC-подписи ссылок. Генерация: openssl rand -base64 32.</summary>
    public string DownloadSigningKey { get; set; } = string.Empty;

    /// <summary>Время жизни ссылки на скачивание.</summary>
    public TimeSpan UrlTtl { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Короткоживущие подписанные ссылки на скачивание вложений через собственный API-эндпоинт
/// (замена presigned URL хранилища: блобы зашифрованы, отдавать их напрямую бессмысленно).
/// Авторизация происходит в момент ВЫДАЧИ ссылки (проверка видимости записи);
/// сам эндпоинт проверяет только подпись и срок — как и у presigned URL раньше.
/// </summary>
public class DownloadTokenService(IOptions<AttachmentDownloadOptions> options)
{
    public string CreateUrl(Guid attachmentId)
    {
        var expiresAtUnix = DateTimeOffset.UtcNow.Add(options.Value.UrlTtl).ToUnixTimeSeconds();
        var signature = Sign(attachmentId, expiresAtUnix);
        return $"/api/attachments/{attachmentId}/file?expires={expiresAtUnix}&sig={signature}";
    }

    public bool Validate(Guid attachmentId, long expiresAtUnix, string signature)
    {
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresAtUnix)
            return false;

        var expected = Sign(attachmentId, expiresAtUnix);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature));
    }

    private string Sign(Guid attachmentId, long expiresAtUnix)
    {
        var key = options.Value.DownloadSigningKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Attachments:DownloadSigningKey не задан — выдача ссылок невозможна.");

        var payload = $"{attachmentId}:{expiresAtUnix}";
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash);
    }
}
