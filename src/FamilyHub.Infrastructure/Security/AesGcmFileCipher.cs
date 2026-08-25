using System.Security.Cryptography;
using System.Text;

namespace FamilyHub.Infrastructure.Security;

/// <summary>
/// AES-256-GCM для файлов целиком (сканы/PDF ограничены лимитом загрузки — буферизация в
/// памяти приемлема и даёт аутентичность всего блоба одним тегом). Ключ на запись/чтение
/// резолвится через <see cref="IEncryptionKeyRing"/> (ADR-0009) — пишет всегда активным, читает
/// по keyId, зашитому в заголовок блоба.
/// </summary>
public class AesGcmFileCipher : IFileCipher
{
    private static readonly byte[] Magic = "FHE1"u8.ToArray();
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly IEncryptionKeyRing _keyRing;
    private readonly byte[] _activeKeyIdBytes;

    public AesGcmFileCipher(IEncryptionKeyRing keyRing)
    {
        _keyRing = keyRing;
        _activeKeyIdBytes = Encoding.UTF8.GetBytes(keyRing.ActiveKeyId);
        if (_activeKeyIdBytes.Length > byte.MaxValue)
            throw new InvalidOperationException("Encryption:ActiveKeyId слишком длинный (максимум 255 байт UTF-8).");
    }

    public async Task<long> EncryptAsync(Stream plain, Stream dest, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await plain.CopyToAsync(buffer, ct);
        var plainBytes = buffer.ToArray();

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipherBytes = new byte[plainBytes.Length];
        using (var aes = new AesGcm(_keyRing.ActiveKey, TagSize))
        {
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
        }

        await dest.WriteAsync(Magic, ct);
        dest.WriteByte((byte)_activeKeyIdBytes.Length);
        await dest.WriteAsync(_activeKeyIdBytes, ct);
        await dest.WriteAsync(nonce, ct);
        await dest.WriteAsync(tag, ct);
        await dest.WriteAsync(cipherBytes, ct);

        return Magic.Length + 1 + _activeKeyIdBytes.Length + NonceSize + TagSize + cipherBytes.Length;
    }

    public async Task<Stream> DecryptAsync(Stream stored, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await stored.CopyToAsync(buffer, ct);
        var data = buffer.ToArray();

        var minLength = Magic.Length + 1 + NonceSize + TagSize;
        if (data.Length < minLength || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidOperationException("Блоб не является зашифрованным вложением (нет заголовка FHE1).");

        var offset = Magic.Length;
        int keyIdLength = data[offset++];
        var keyId = Encoding.UTF8.GetString(data, offset, keyIdLength);
        offset += keyIdLength;
        var key = _keyRing.ForKeyId(keyId);

        var nonce = data.AsSpan(offset, NonceSize);
        offset += NonceSize;
        var tag = data.AsSpan(offset, TagSize);
        offset += TagSize;
        var cipherBytes = data.AsSpan(offset);
        var plainBytes = new byte[cipherBytes.Length];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
        }

        return new MemoryStream(plainBytes, writable: false);
    }
}
