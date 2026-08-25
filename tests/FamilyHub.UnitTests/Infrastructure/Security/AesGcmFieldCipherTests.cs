using FamilyHub.Infrastructure.Security;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Security;

public class AesGcmFieldCipherTests
{
    private const string Key = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

    private static AesGcmFieldCipher CreateSut(string? key = null, string keyId = "v1") =>
        new(new EncryptionKeyRing(new EncryptionOptions
        {
            MasterKey = key ?? Key,
            ActiveKeyId = keyId,
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
    public void Unprotect_PlaintextValue_AlwaysThrows()
    {
        // Проект ещё в разработке — старых БД с "переходным периодом" не существует (пересоздаются
        // с нуля), поэтому legacy-чтение незашифрованных значений не поддерживается вовсе:
        // запись всегда шифрует, чтение всегда требует префикс "enc:".
        var act = () => CreateSut().Unprotect("plaintext");

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

    // --- Связка ключей (ADR-0009): активный + отставные ---

    [Fact]
    public void Unprotect_ValueEncryptedWithPreviousKey_StillReadable()
    {
        // v1 писал/читал сам себя, пока был активным...
        var v1Ring = new EncryptionKeyRing(new EncryptionOptions { MasterKey = Key, ActiveKeyId = "v1" });
        var stored = new AesGcmFieldCipher(v1Ring).Protect("секрет");

        // ...после ротации v1 переехал в отставные, активен v2 — значение по-прежнему читается.
        var otherKey = Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray());
        var rotatedRing = new EncryptionKeyRing(new EncryptionOptions
        {
            MasterKey = otherKey,
            ActiveKeyId = "v2",
            PreviousKeys = [new EncryptionKeyEntry { Id = "v1", Material = Key }],
        });

        new AesGcmFieldCipher(rotatedRing).Unprotect(stored).Should().Be("секрет");
    }

    [Fact]
    public void Protect_AfterRotation_WritesWithNewActiveKeyId()
    {
        var otherKey = Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray());
        var rotatedRing = new EncryptionKeyRing(new EncryptionOptions
        {
            MasterKey = otherKey,
            ActiveKeyId = "v2",
            PreviousKeys = [new EncryptionKeyEntry { Id = "v1", Material = Key }],
        });

        new AesGcmFieldCipher(rotatedRing).Protect("секрет").Should().StartWith("enc:v2:");
    }

    [Fact]
    public void DuplicateKeyIdBetweenActiveAndPrevious_ThrowsOnConstruction()
    {
        var act = () => new EncryptionKeyRing(new EncryptionOptions
        {
            MasterKey = Key,
            ActiveKeyId = "v1",
            PreviousKeys = [new EncryptionKeyEntry { Id = "v1", Material = Key }],
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*v1*");
    }

    [Fact]
    public void DuplicateKeyIdBetweenTwoPreviousKeys_ThrowsOnConstruction()
    {
        var act = () => new EncryptionKeyRing(new EncryptionOptions
        {
            MasterKey = Key,
            ActiveKeyId = "v3",
            PreviousKeys =
            [
                new EncryptionKeyEntry { Id = "v1", Material = Key },
                new EncryptionKeyEntry { Id = "v1", Material = Key },
            ],
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*v1*");
    }
}
