using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FamilyHub.Infrastructure.Security.Credentials;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Security.Credentials;

/// <summary>
/// Криптографические примитивы ротации учёток (ADR-0011): формат admin-протокола MinIO, подпись
/// SigV4, SCRAM-верификатор, генератор секретов. Совместимость с СЕРВЕРАМИ проверена отдельно:
/// шифр — расшифровкой настоящим madmin-go (вектор ниже) и реальным MinIO (интеграционные тесты),
/// SCRAM — реальным Postgres (интеграционные тесты).
/// </summary>
public class CredentialCryptoTests
{
    // ─── MadminPayloadCipher ────────────────────────────────────────────────────────────────────

    private const string VectorPassword = "vector-password-Ω-not-ascii-ok";
    private static readonly byte[] VectorSalt = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] VectorNonce = Enumerable.Range(101, 8).Select(i => (byte)i).ToArray();

    private static byte[] Pattern(int size) => Enumerable.Range(0, size).Select(i => (byte)(i * 31 % 251)).ToArray();

    /// <summary>
    /// «Золотой» вектор: шифртекст 137 байт с фиксированными salt/nonce/паролем. Эти байты расшифрованы
    /// настоящим madmin.DecryptData из madmin-go v3.0.109 (той самой функцией, что на стороне сервера
    /// MinIO) — вместе с пустым, ровно-16-КиБ и трёхфрагментным вариантами. Если тест упал после
    /// правки шифра — совместимость с MinIO сломана, а не «тест устарел».
    /// </summary>
    private const string GoldenSmallHex =
        "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f200265666768696a6b6c" +
        "16b81482c05b948fdfcc159541b4453eaeb8d068120798055370e48feb8d84966a6bd508a5f143781d8c72282dc6f4" +
        "53512bf15629b86d2c20edcc26ae69b7751b155dfe90c775f95f11e4bbe998c0e525190997e4e93666322be4473d7d" +
        "271d91867a82b5325ee5eaadccf39a93864d71d36c74f428d2720eb9efeb6e25bf24688bcf75280513dda2737a1363" +
        "1f320cb0f5c0a2874d9056aa";

    [Fact]
    public void MadminCipher_MatchesVectorVerifiedAgainstMadminGo()
    {
        var encrypted = MadminPayloadCipher.Encrypt(VectorPassword, Pattern(137), VectorSalt, VectorNonce);

        Convert.ToHexString(encrypted).ToLowerInvariant().Should().Be(GoldenSmallHex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(137)]
    [InlineData(16383)]
    [InlineData(16384)]
    [InlineData(16385)]
    [InlineData(40000)]
    public void MadminCipher_RoundTrips_AtFragmentBoundaries(int size)
    {
        var plain = Pattern(size);

        var encrypted = MadminPayloadCipher.Encrypt("secret", plain);

        MadminPayloadCipher.Decrypt("secret", encrypted).Should().Equal(plain);
    }

    [Fact]
    public void MadminCipher_Layout_IsSaltIdNonceThenFragmentsWithTags()
    {
        var encrypted = MadminPayloadCipher.Encrypt("secret", Pattern(40000), VectorSalt, VectorNonce);

        encrypted[..32].Should().Equal(VectorSalt);
        encrypted[32].Should().Be(0x02, "id 0x02 = pbkdf2 + AES-GCM — единственный вариант без Argon2, который принимает сервер");
        encrypted[33..41].Should().Equal(VectorNonce);
        encrypted.Length.Should().Be(32 + 1 + 8 + 40000 + 3 * 16, "три фрагмента (16384 + 16384 + 7232), у каждого 16-байтный тег");
    }

    [Fact]
    public void MadminCipher_IsRandomised_ByDefault()
    {
        var a = MadminPayloadCipher.Encrypt("secret", Pattern(50));
        var b = MadminPayloadCipher.Encrypt("secret", Pattern(50));

        a.Should().NotEqual(b, "salt и nonce случайные — одинаковые тела не должны давать одинаковый шифртекст");
    }

    [Fact]
    public void MadminCipher_RejectsWrongPassword_AndTamperedData()
    {
        var encrypted = MadminPayloadCipher.Encrypt("secret", Pattern(100));

        var wrongPassword = () => MadminPayloadCipher.Decrypt("other", encrypted);
        wrongPassword.Should().Throw<CryptographicException>();

        var tampered = (byte[])encrypted.Clone();
        tampered[^1] ^= 0x01;
        var tamperedAct = () => MadminPayloadCipher.Decrypt("secret", tampered);
        tamperedAct.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void MadminCipher_TruncatedStream_IsRejected()
    {
        // Флаг «последний фрагмент» входит в AAD: если отрезать хвостовой фрагмент, предыдущий
        // оказывается «последним» с чужим флагом и не проходит проверку — обрезать запрос нельзя.
        var encrypted = MadminPayloadCipher.Encrypt("secret", Pattern(40000));
        var truncated = encrypted[..(encrypted.Length - (7232 + 16))];

        var act = () => MadminPayloadCipher.Decrypt("secret", truncated);

        act.Should().Throw<CryptographicException>();
    }

    // ─── AwsSigV4Signer ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SigV4_MatchesAwsTestSuite_GetVanilla()
    {
        // Вектор из официального «AWS Signature Version 4 Test Suite» (get-vanilla).
        var headers = new Dictionary<string, string>
        {
            ["host"] = "example.amazonaws.com",
            ["x-amz-date"] = "20150830T123600Z",
        };

        var authorization = AwsSigV4Signer.Authorization(
            "GET", new Uri("https://example.amazonaws.com/"), headers,
            payloadHashHex: AwsSigV4Signer.Sha256Hex([]),
            accessKey: "AKIDEXAMPLE", secretKey: "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY",
            region: "us-east-1", service: "service", utcNow: new DateTime(2015, 8, 30, 12, 36, 0, DateTimeKind.Utc));

        authorization.Should().Be(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, " +
            "SignedHeaders=host;x-amz-date, " +
            "Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31");
    }

    [Fact]
    public void SigV4_QueryParameters_AreSortedAndEncoded_SoOrderDoesNotChangeTheSignature()
    {
        var headers = new Dictionary<string, string> { ["host"] = "minio:9000", ["x-amz-date"] = "20260924T120000Z" };
        var now = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
        string Sign(string query) => AwsSigV4Signer.Authorization("GET", new Uri("http://minio:9000/minio/admin/v3/x?" + query),
            headers, AwsSigV4Signer.Sha256Hex([]), "ak", "sk", "us-east-1", "s3", now);

        Sign("b=2&a=1").Should().Be(Sign("a=1&b=2"));
        Sign("accessKey=FHAPP%2B1").Should().NotBe(Sign("accessKey=FHAPP1"), "значение параметра входит в подпись");
    }

    // ─── ScramSha256Verifier ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Scram_HasPostgresFormat_AndNeverContainsThePassword()
    {
        var verifier = ScramSha256Verifier.Create("correct-horse_battery-staple");

        verifier.Should().MatchRegex(@"^SCRAM-SHA-256\$4096:[A-Za-z0-9+/=]+\$[A-Za-z0-9+/=]+:[A-Za-z0-9+/=]+$");
        verifier.Should().NotContain("correct-horse");
    }

    [Fact]
    public void Scram_IsDeterministicForTheSameSalt_AndSaltedOtherwise()
    {
        var salt = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

        ScramSha256Verifier.Create("pw-1", salt).Should().Be(ScramSha256Verifier.Create("pw-1", salt));
        ScramSha256Verifier.Create("pw-1", salt).Should().NotBe(ScramSha256Verifier.Create("pw-2", salt));
        ScramSha256Verifier.Create("pw-1").Should().NotBe(ScramSha256Verifier.Create("pw-1"), "соль случайная");
    }

    [Theory]
    [InlineData("пароль")]
    [InlineData("with space")]
    [InlineData("tab\there")]
    public void Scram_RejectsPasswordsSaslprepWouldChange(string password)
    {
        var act = () => ScramSha256Verifier.Create(password);

        act.Should().Throw<ArgumentException>();
    }

    // ─── SecretGenerator ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Generator_PostgresPassword_IsBase64Url_AndSafeInConnectionStringsAndCompose()
    {
        var passwords = Enumerable.Range(0, 200).Select(_ => SecretGenerator.PostgresPassword()).ToList();

        passwords.Should().OnlyContain(p => p.Length == 43 && Regex.IsMatch(p, "^[A-Za-z0-9_-]+$"));
        passwords.Distinct().Should().HaveCount(200);
    }

    [Fact]
    public void Generator_MinioKeys_FitMinioLimitsAndPrefix()
    {
        var accessKeys = Enumerable.Range(0, 200).Select(_ => SecretGenerator.MinioAccessKey()).ToList();
        var secretKeys = Enumerable.Range(0, 200).Select(_ => SecretGenerator.MinioSecretKey()).ToList();

        accessKeys.Should().OnlyContain(k => k.Length == 20 && k.StartsWith("FHAPP") && Regex.IsMatch(k, "^[A-Z0-9]+$"));
        secretKeys.Should().OnlyContain(k => k.Length == 40 && Regex.IsMatch(k, "^[A-Za-z0-9]+$"));
        accessKeys.Distinct().Should().HaveCount(200);
        secretKeys.Distinct().Should().HaveCount(200);
    }
}
