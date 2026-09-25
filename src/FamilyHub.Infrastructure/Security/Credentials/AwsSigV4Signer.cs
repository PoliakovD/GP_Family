using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FamilyHub.Infrastructure.Security.Credentials;

/// <summary>
/// AWS Signature Version 4 — подпись запросов к admin API MinIO (сервис «s3», регион us-east-1, как у
/// madmin-go: пустой регион там превращается в us-east-1). SDK minio-dotnet публичного подписчика не
/// даёт, а тянуть AWS SDK ради одного заголовка незачем. Реализация проверяется тестовым вектором
/// из официального AWS Signature V4 Test Suite (get-vanilla).
/// </summary>
public static class AwsSigV4Signer
{
    public const string Algorithm = "AWS4-HMAC-SHA256";

    public static string AmzDate(DateTime utcNow) =>
        utcNow.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Значение заголовка Authorization.</summary>
    /// <param name="headers">Подписываемые заголовки: имя в нижнем регистре → значение. Обязательно
    /// содержат host и x-amz-date (и x-amz-content-sha256 для MinIO).</param>
    /// <param name="payloadHashHex">Hex SHA-256 тела (у пустого — хэш пустой строки).</param>
    public static string Authorization(
        string method, Uri uri, IReadOnlyDictionary<string, string> headers, string payloadHashHex,
        string accessKey, string secretKey, string region, string service, DateTime utcNow)
    {
        var amzDate = AmzDate(utcNow);
        var date = amzDate[..8];
        var scope = $"{date}/{region}/{service}/aws4_request";

        var signedNames = headers.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var canonicalHeaders = string.Concat(signedNames.Select(k => $"{k}:{Trim(headers[k])}\n"));
        var signedHeaders = string.Join(';', signedNames);

        var canonicalRequest = string.Join('\n',
            method.ToUpperInvariant(),
            CanonicalPath(uri),
            CanonicalQuery(uri),
            canonicalHeaders,
            signedHeaders,
            payloadHashHex);

        var stringToSign = string.Join('\n', Algorithm, amzDate, scope, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var kDate = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date);
        var kRegion = Hmac(kDate, region);
        var kService = Hmac(kRegion, service);
        var kSigning = Hmac(kService, "aws4_request");
        var signature = Hex(Hmac(kSigning, stringToSign));

        return $"{Algorithm} Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Hex(SHA256.HashData(data));

    // Для s3-совместимых сервисов путь кодируется один раз (без двойного кодирования сегментов).
    private static string CanonicalPath(Uri uri)
    {
        var path = uri.AbsolutePath;
        return path.Length == 0 ? "/" : string.Join('/', path.Split('/').Select(s => Encode(Uri.UnescapeDataString(s))));
    }

    private static string CanonicalQuery(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Query)) return string.Empty;
        var pairs = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p =>
        {
            var i = p.IndexOf('=');
            var name = i < 0 ? p : p[..i];
            var value = i < 0 ? string.Empty : p[(i + 1)..];
            return (Name: Encode(Uri.UnescapeDataString(name.Replace('+', ' '))), Value: Encode(Uri.UnescapeDataString(value.Replace('+', ' '))));
        });
        return string.Join('&', pairs.OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"{p.Name}={p.Value}"));
    }

    /// <summary>RFC 3986: не кодируются только A-Za-z0-9 - _ . ~ .</summary>
    private static string Encode(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~')
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static string Trim(string value) => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
