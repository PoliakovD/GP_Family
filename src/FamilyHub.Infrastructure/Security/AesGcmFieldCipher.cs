using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Security;

/// <summary>
/// AES-256-GCM для строковых полей: случайный 12-байтовый nonce на каждое значение,
/// 16-байтовый тег аутентичности. Подмена/порча шифротекста ломает тег → исключение,
/// а не тихое чтение мусора.
/// </summary>
public class AesGcmFieldCipher : IFieldCipher
{
    private const string Prefix = "enc";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;
    private readonly string _keyId;
    private readonly bool _allowLegacyPlaintextRead;

    public AesGcmFieldCipher(IOptions<EncryptionOptions> options)
    {
        var opts = options.Value;
        _key = DecodeKey(opts.MasterKey);
        _keyId = opts.ActiveKeyId;
        _allowLegacyPlaintextRead = opts.AllowLegacyPlaintextRead;
    }

    internal static byte[] DecodeKey(string masterKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(masterKeyBase64))
            throw new InvalidOperationException("Encryption:MasterKey не задан — at-rest шифрование невозможно.");

        byte[] key;
        try
        {
            key = Convert.FromBase64String(masterKeyBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Encryption:MasterKey не является корректным base64.", ex);
        }

        if (key.Length != 32)
            throw new InvalidOperationException(
                $"Encryption:MasterKey должен быть 32 байта (AES-256), получено {key.Length}.");
        return key;
    }

    public string Protect(string plaintext)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipherBytes = new byte[plainBytes.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var packed = new byte[NonceSize + TagSize + cipherBytes.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, NonceSize);
        cipherBytes.CopyTo(packed, NonceSize + TagSize);

        return $"{Prefix}:{_keyId}:{Convert.ToBase64String(packed)}";
    }

    public string Unprotect(string stored)
    {
        if (!stored.StartsWith($"{Prefix}:", StringComparison.Ordinal))
        {
            // Строки, созданные до внедрения шифрования. На записи всегда шифруем,
            // так что доля legacy-значений только убывает.
            if (_allowLegacyPlaintextRead) return stored;
            throw new InvalidOperationException("Обнаружено незашифрованное значение при запрете legacy-чтения.");
        }

        var parts = stored.Split(':', 3);
        if (parts.Length != 3)
            throw new InvalidOperationException("Повреждённый формат зашифрованного значения.");
        if (parts[1] != _keyId)
            throw new InvalidOperationException(
                $"Значение зашифровано ключом «{parts[1]}», активен «{_keyId}» — требуется ротационная перешифровка.");

        var packed = Convert.FromBase64String(parts[2]);
        var nonce = packed.AsSpan(0, NonceSize);
        var tag = packed.AsSpan(NonceSize, TagSize);
        var cipherBytes = packed.AsSpan(NonceSize + TagSize);
        var plainBytes = new byte[cipherBytes.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }
}
