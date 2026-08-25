using System.Security.Cryptography;
using System.Text;

namespace FamilyHub.Infrastructure.Security;

/// <summary>
/// AES-256-GCM для строковых полей: случайный 12-байтовый nonce на каждое значение,
/// 16-байтовый тег аутентичности. Подмена/порча шифротекста ломает тег → исключение,
/// а не тихое чтение мусора. Ключ на запись/чтение резолвится через <see cref="IEncryptionKeyRing"/>
/// (ADR-0009) — пишет всегда активным, читает по keyId, зашитому в само значение.
/// </summary>
public class AesGcmFieldCipher : IFieldCipher
{
    private const string Prefix = "enc";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly IEncryptionKeyRing _keyRing;

    public AesGcmFieldCipher(IEncryptionKeyRing keyRing)
    {
        _keyRing = keyRing;
    }

    public string Protect(string plaintext)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipherBytes = new byte[plainBytes.Length];

        using var aes = new AesGcm(_keyRing.ActiveKey, TagSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var packed = new byte[NonceSize + TagSize + cipherBytes.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, NonceSize);
        cipherBytes.CopyTo(packed, NonceSize + TagSize);

        return $"{Prefix}:{_keyRing.ActiveKeyId}:{Convert.ToBase64String(packed)}";
    }

    public string Unprotect(string stored)
    {
        if (!stored.StartsWith($"{Prefix}:", StringComparison.Ordinal))
            throw new InvalidOperationException("Значение не зашифровано (нет префикса \"enc:\").");

        var parts = stored.Split(':', 3);
        if (parts.Length != 3)
            throw new InvalidOperationException("Повреждённый формат зашифрованного значения.");

        var key = _keyRing.ForKeyId(parts[1]);

        var packed = Convert.FromBase64String(parts[2]);
        var nonce = packed.AsSpan(0, NonceSize);
        var tag = packed.AsSpan(NonceSize, TagSize);
        var cipherBytes = packed.AsSpan(NonceSize + TagSize);
        var plainBytes = new byte[cipherBytes.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }
}
