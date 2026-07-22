using FamilyHub.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Security;

public class AesGcmFieldCipherTests
{
    private const string Key = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

    private static AesGcmFieldCipher CreateSut(
        string? key = null, string keyId = "v1", bool allowLegacy = true) =>
        new(Options.Create(new EncryptionOptions
        {
            MasterKey = key ?? Key,
            ActiveKeyId = keyId,
            AllowLegacyPlaintextRead = allowLegacy,
        }));

    [Fact]
    public void ProtectUnprotect_RoundTripsUnicode()
    {
        var sut = CreateSut();

        var stored = sut.Protect("Иванов Иван, диагноз ①");

        stored.Should().StartWith("enc:v1:");
        sut.Unprotect(stored).Should().Be("Иванов Иван, диагноз ①");
    }

    [Fact]
    public void Protect_SamePlaintextTwice_ProducesDifferentCiphertext()
    {
        var sut = CreateSut();

        // Случайный nonce: одинаковые значения не должны давать одинаковый шифротекст
        // (иначе по БД видно, у кого совпадают ФИО).
        sut.Protect("Иванов").Should().NotBe(sut.Protect("Иванов"));
    }

    [Fact]
    public void Unprotect_TamperedCiphertext_Throws()
    {
        var sut = CreateSut();
        var stored = sut.Protect("секрет");
        var tampered = stored[..^4] + (stored[^4] == 'A' ? "BBBB" : "AAAA");

        var act = () => sut.Unprotect(tampered);

        act.Should().Throw<Exception>("GCM-тег обязан поймать подмену");
    }

    [Fact]
    public void Unprotect_WrongKey_Throws()
    {
        var stored = CreateSut().Protect("секрет");
        var otherKey = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());

        var act = () => CreateSut(key: otherKey).Unprotect(stored);

        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Unprotect_LegacyPlaintext_PassesThroughWhenAllowed()
    {
        CreateSut().Unprotect("до-шифровальное значение").Should().Be("до-шифровальное значение");
    }

    [Fact]
    public void Unprotect_LegacyPlaintext_ThrowsWhenDisallowed()
    {
        var act = () => CreateSut(allowLegacy: false).Unprotect("plaintext");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Unprotect_UnknownKeyId_Throws()
    {
        var stored = CreateSut(keyId: "v9").Protect("секрет");

        var act = () => CreateSut(keyId: "v1").Unprotect(stored);

        act.Should().Throw<InvalidOperationException>().WithMessage("*v9*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("не-base64")]
    [InlineData("c2hvcnQ=")] // "short" — не 32 байта
    public void InvalidMasterKey_ThrowsOnConstruction(string badKey)
    {
        var act = () => CreateSut(key: badKey);

        act.Should().Throw<InvalidOperationException>();
    }
}
