namespace FamilyHub.Infrastructure.Security;

/// <summary>
/// Шифрование файлов-вложений (сканы, PDF) целиком перед записью в объектное хранилище.
/// Формат: magic "FHE1" + keyId(1 байт длины + utf8) + nonce(12) + tag(16) + ciphertext.
/// </summary>
public interface IFileCipher
{
    /// <summary>Шифрует plain в dest. Возвращает итоговый размер зашифрованного блоба.</summary>
    Task<long> EncryptAsync(Stream plain, Stream dest, CancellationToken ct = default);

    /// <summary>Расшифровывает stored (читает до конца) и возвращает поток открытых данных.</summary>
    Task<Stream> DecryptAsync(Stream stored, CancellationToken ct = default);
}
