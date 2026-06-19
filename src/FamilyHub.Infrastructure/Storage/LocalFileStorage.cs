using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Storage;

/// <summary>
/// Временная локальная реализация IFileStorage — пишет на диск и выдаёт подписанную
/// короткоживущую ссылку (HMAC по storageKey+expiry), имитируя pre-signed URL MinIO.
/// Заменяется на реальный MinIO-клиент в этапе 2 п.9 без изменений в вызывающем коде.
/// </summary>
public class LocalFileStorage(IOptions<LocalFileStorageOptions> options) : IFileStorage
{
    public async Task<string> SaveAsync(string storageKey, Stream content, string contentType, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var file = File.Create(fullPath);
        await content.CopyToAsync(file, ct);

        return storageKey;
    }

    public Task<string> GetPresignedUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct = default)
    {
        var expiresAtUnix = DateTimeOffset.UtcNow.Add(expiry).ToUnixTimeSeconds();
        var signature = Sign(storageKey, expiresAtUnix);

        // storageKey управляется сервером (Guid + безопасные имена), поэтому не экранируем
        // его целиком — иначе '/' превратится в %2F и не совпадёт с catch-all маршрутом.
        var url = $"{options.Value.PublicBasePath}/{storageKey}" +
                  $"?expires={expiresAtUnix}&sig={signature}";

        return Task.FromResult(url);
    }

    /// <summary>Проверка подписи и срока действия — используется эндпоинтом, отдающим файл.</summary>
    public bool IsValidSignature(string storageKey, long expiresAtUnix, string signature)
    {
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresAtUnix)
            return false;

        var expected = Sign(storageKey, expiresAtUnix);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature));
    }

    public string ResolvePath(string storageKey) =>
        Path.Combine(Path.GetFullPath(options.Value.RootPath), storageKey.Replace('/', Path.DirectorySeparatorChar));

    private string Sign(string storageKey, long expiresAtUnix)
    {
        var payload = $"{storageKey}:{expiresAtUnix}";
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Value.SigningKey), Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash);
    }
}
