using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FamilyHub.Infrastructure.Security.Credentials;

/// <summary>
/// Шифрование ТЕЛА запросов admin API MinIO (madmin): сервер отвергает открытый JSON на
/// add-service-account и т.п. — тело шифруется секретным ключом вызывающего. SDK minio-dotnet admin API
/// не содержит, поэтому формат реализован здесь по исходникам madmin-go (encrypt.go) и sio-go
/// (writer.go); совместимость проверяется реальным сервером в интеграционных тестах и расшифровкой
/// самим madmin-go.
///
/// Формат: <c>salt[32] ‖ id[1] ‖ nonce[8] ‖ фрагменты</c>.
///  - Ключ: PBKDF2-HMAC-SHA256(пароль, salt, 8192 итерации, 32 байта). Используем вариант id=0x02
///    (pbkdf2AESGCM), который сервер принимает при расшифровке, — он не требует Argon2 (в .NET нет).
///  - Поток — sio-go, AES-256-GCM, фрагменты по 16 КиБ открытого текста + 16 байт тега. Nonce
///    фрагмента — 12 байт: nonce[8] ‖ uint32 счётчика в LITTLE-endian. Associated data фрагмента —
///    флаг (0x00 для обычного, 0x80 для последнего) ‖ 16-байтный «тег заголовка».
///  - «Тег заголовка» — тег AES-GCM над ПУСТЫМ сообщением с nonce счётчика 0 (и пустым AAD); данные
///    шифруются со счётчиком 1, 2, ….
/// </summary>
public static class MadminPayloadCipher
{
    private const byte Pbkdf2AesGcm = 0x02;
    private const int SaltSize = 32;
    private const int NonceSize = 8;
    private const int TagSize = 16;
    private const int Pbkdf2Iterations = 8192;
    private const int FragmentSize = 1 << 14; // sio.BufSize

    /// <param name="salt">Только для детерминированных тестов; по умолчанию случайная.</param>
    /// <param name="nonce">Только для детерминированных тестов; по умолчанию случайный.</param>
    public static byte[] Encrypt(string password, ReadOnlySpan<byte> plaintext, byte[]? salt = null, byte[]? nonce = null)
    {
        salt ??= RandomNumberGenerator.GetBytes(SaltSize);
        nonce ??= RandomNumberGenerator.GetBytes(NonceSize);
        if (salt.Length != SaltSize) throw new ArgumentException("salt must be 32 bytes", nameof(salt));
        if (nonce.Length != NonceSize) throw new ArgumentException("nonce must be 8 bytes", nameof(nonce));

        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        using var aes = new AesGcm(key, TagSize);

        var aad = HeaderAad(aes, nonce);

        // Последний фрагмент — всегда непустой остаток 1..FragmentSize (пустой — только у пустого
        // сообщения), как у sio-go: цикл `len(p) > bufSize` оставляет хвост буферу до Close().
        var fragments = plaintext.Length == 0 ? 1 : (plaintext.Length + FragmentSize - 1) / FragmentSize;
        var output = new byte[SaltSize + 1 + NonceSize + plaintext.Length + fragments * TagSize];
        salt.CopyTo(output, 0);
        output[SaltSize] = Pbkdf2AesGcm;
        nonce.CopyTo(output, SaltSize + 1);

        var offset = SaltSize + 1 + NonceSize;
        for (var i = 0; i < fragments; i++)
        {
            aad[0] = i == fragments - 1 ? (byte)0x80 : (byte)0x00;
            var chunk = plaintext.Slice(i * FragmentSize, Math.Min(FragmentSize, plaintext.Length - i * FragmentSize));
            aes.Encrypt(FragmentNonce(nonce, (uint)(i + 1)), chunk,
                output.AsSpan(offset, chunk.Length), output.AsSpan(offset + chunk.Length, TagSize), aad);
            offset += chunk.Length + TagSize;
        }

        return output;
    }

    /// <summary>Обратная операция — нужна тестам (проверка формата в обе стороны). Сервер MinIO
    /// шифрует ответы вариантом Argon2id, который здесь не поддержан — приложение ответы admin API
    /// не читает (см. MinioAdminClient).</summary>
    public static byte[] Decrypt(string password, ReadOnlySpan<byte> data)
    {
        if (data.Length < SaltSize + 1 + NonceSize) throw new CryptographicException("unexpected header");
        if (data[SaltSize] != Pbkdf2AesGcm) throw new CryptographicException("unsupported madmin algorithm id");

        var salt = data[..SaltSize];
        var nonce = data.Slice(SaltSize + 1, NonceSize).ToArray();
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        using var aes = new AesGcm(key, TagSize);
        var aad = HeaderAad(aes, nonce);

        var body = data[(SaltSize + 1 + NonceSize)..];
        var result = new List<byte>();
        var counter = 1u;
        while (body.Length > 0)
        {
            var take = Math.Min(FragmentSize + TagSize, body.Length);
            var cipherLen = take - TagSize;
            if (cipherLen < 0) throw new CryptographicException("truncated fragment");
            aad[0] = take == body.Length ? (byte)0x80 : (byte)0x00;
            var plain = new byte[cipherLen];
            aes.Decrypt(FragmentNonce(nonce, counter++), body.Slice(0, cipherLen), body.Slice(cipherLen, TagSize), plain, aad);
            result.AddRange(plain);
            body = body[take..];
        }

        return [.. result];
    }

    /// <summary>[флаг] ‖ «тег заголовка»: тег GCM над пустым сообщением, nonce со счётчиком 0.</summary>
    private static byte[] HeaderAad(AesGcm aes, byte[] nonce8)
    {
        var aad = new byte[1 + TagSize];
        aes.Encrypt(FragmentNonce(nonce8, 0), ReadOnlySpan<byte>.Empty, Span<byte>.Empty, aad.AsSpan(1, TagSize));
        return aad;
    }

    private static byte[] FragmentNonce(byte[] nonce8, uint counter)
    {
        var nonce = new byte[12];
        nonce8.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8), counter);
        return nonce;
    }
}
