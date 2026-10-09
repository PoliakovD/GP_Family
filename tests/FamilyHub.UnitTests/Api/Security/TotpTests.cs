using System.Text;
using FamilyHub.Api.Configuration;
using FamilyHub.Api.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FamilyHub.UnitTests.Api.Security;

/// <summary>TOTP для входа в админ-панель (аудит security-audit-2026-10, бэклог M3).</summary>
public class TotpTests
{
    // RFC 6238, приложение B: секрет "12345678901234567890" (ASCII), HMAC-SHA1; ожидаемые 8-значные
    // коды усечены до последних 6 цифр (6-значный вариант того же HOTP).
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");
    private const string RfcSecretBase32 = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void Compute_MatchesRfc6238Vectors(long unixSeconds, string expected) =>
        Totp.Compute(RfcSecret, Totp.TimeStep(DateTimeOffset.FromUnixTimeSeconds(unixSeconds))).Should().Be(expected);

    [Theory]
    [InlineData(RfcSecretBase32)]
    [InlineData("gezdgnbvgy3tqojqgezdgnbvgy3tqojq")]
    [InlineData("GEZD GNBV GY3T QOJQ GEZD GNBV GY3T QOJQ")]
    [InlineData("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ====")]
    public void DecodeBase32_AcceptsCommonForms(string value) =>
        Totp.DecodeBase32(value).Should().Equal(RfcSecret);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base32!")]
    [InlineData("0189")]
    public void DecodeBase32_Invalid_Null(string? value) =>
        Totp.DecodeBase32(value).Should().BeNull();

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (AdminTotpVerifier Sut, ManualTimeProvider Time) CreateVerifier(long unixSeconds = 1234567890)
    {
        var time = new ManualTimeProvider(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
        var options = Options.Create(new AdminOptions { TotpSecret = RfcSecretBase32 });
        return (new AdminTotpVerifier(options, time), time);
    }

    private static string CodeAt(DateTimeOffset time) => Totp.Compute(RfcSecret, Totp.TimeStep(time));

    [Fact]
    public void Verify_CurrentCode_Accepted_ButNotTwice()
    {
        var (sut, time) = CreateVerifier();
        var code = CodeAt(time.Now);

        sut.Verify(code).Should().BeTrue();
        sut.Verify(code).Should().BeFalse("один и тот же код нельзя использовать повторно");
    }

    [Fact]
    public void Verify_PreviousAndNextStep_AcceptedForClockSkew()
    {
        var (sut, time) = CreateVerifier();
        sut.Verify(CodeAt(time.Now - Totp.Step)).Should().BeTrue();

        var (sut2, time2) = CreateVerifier();
        sut2.Verify(CodeAt(time2.Now + Totp.Step)).Should().BeTrue();
    }

    [Fact]
    public void Verify_CodeTwoStepsOld_Rejected()
    {
        var (sut, time) = CreateVerifier();

        sut.Verify(CodeAt(time.Now - Totp.Step * 2)).Should().BeFalse();
    }

    [Fact]
    public void Verify_AfterAcceptingNewerStep_OlderCodeRejected()
    {
        var (sut, time) = CreateVerifier();
        var olderCode = CodeAt(time.Now - Totp.Step);

        sut.Verify(CodeAt(time.Now)).Should().BeTrue();
        sut.Verify(olderCode).Should().BeFalse("код из более раннего шага после входа уже не принимается");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public void Verify_MalformedCode_Rejected(string? code) =>
        CreateVerifier().Sut.Verify(code).Should().BeFalse();

    [Fact]
    public void Verify_NoSecretConfigured_Rejected()
    {
        var sut = new AdminTotpVerifier(Options.Create(new AdminOptions()), TimeProvider.System);

        sut.Verify("123456").Should().BeFalse();
    }
}
