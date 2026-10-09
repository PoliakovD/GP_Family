using System.Security.Cryptography;

namespace FamilyHub.Api.Security;

/// <summary>
/// TOTP по RFC 6238 (HMAC-SHA1, шаг 30 с, 6 цифр) — те же параметры по умолчанию, что у Google
/// Authenticator/Aegis/1Password, поэтому секрет добавляется в любое приложение-аутентификатор как есть.
/// Своя реализация вместо пакета: это ~50 строк по стандарту, а зависимость ради них не нужна.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    public static long TimeStep(DateTimeOffset time) => time.ToUnixTimeSeconds() / (long)Step.TotalSeconds;

    /// <summary>Код для шага <paramref name="timeStep"/> (RFC 4226 HOTP с динамическим усечением).</summary>
    public static string Compute(byte[] secret, long timeStep)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, timeStep);
        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(secret, counter, hash);

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>Base32 (RFC 4648) без учёта регистра, пробелов и '='-паддинга; null — не base32.</summary>
    public static byte[]? DecodeBase32(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var cleaned = value.Replace(" ", "").Replace("-", "").TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>(cleaned.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in cleaned)
        {
            var index = alphabet.IndexOf(c);
            if (index < 0) return null;
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }
        return output.Count == 0 ? null : output.ToArray();
    }
}
