using System.Text;
using FamilyHub.Infrastructure.Security;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Security;

public class AesGcmFileCipherTests
{
    private const string Key = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

    private static AesGcmFileCipher CreateSut(string keyId = "v1") =>
        new(new EncryptionKeyRing(new EncryptionOptions { MasterKey = Key, ActiveKeyId = keyId }));

    [Fact]
    public async Task EncryptDecrypt_RoundTripsBytes()
    {
        var sut = CreateSut();
        var original = Encoding.UTF8.GetBytes("PDF-скан с результатами анализов");
        using var encrypted = new MemoryStream();

        var encryptedSize = await sut.EncryptAsync(new MemoryStream(original), encrypted);
        encrypted.Length.Should().Be(encryptedSize);
        encrypted.ToArray().Should().NotContainInOrder(original);

        encrypted.Position = 0;
        await using var decrypted = await sut.DecryptAsync(encrypted);
        using var result = new MemoryStream();
        await decrypted.CopyToAsync(result);

        result.ToArray().Should().Equal(original);
    }

    [Fact]
    public async Task Decrypt_TamperedBlob_Throws()
    {
        var sut = CreateSut();
        using var encrypted = new MemoryStream();
        await sut.EncryptAsync(new MemoryStream("секретные данные"u8.ToArray()), encrypted);
        var data = encrypted.ToArray();
        data[^1] ^= 0xFF;

        var act = async () => await sut.DecryptAsync(new MemoryStream(data));

        await act.Should().ThrowAsync<Exception>("GCM-тег обязан поймать подмену");
    }

    [Fact]
    public async Task Decrypt_NotEncryptedBlob_Throws()
    {
        var act = async () => await CreateSut().DecryptAsync(new MemoryStream("просто pdf"u8.ToArray()));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*FHE1*");
    }

    [Fact]
    public async Task Decrypt_UnknownKeyId_Throws()
    {
        using var encrypted = new MemoryStream();
        await CreateSut(keyId: "v9").EncryptAsync(new MemoryStream("данные"u8.ToArray()), encrypted);
        encrypted.Position = 0;

        var act = async () => await CreateSut(keyId: "v1").DecryptAsync(encrypted);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*v9*");
    }

    [Fact]
    public async Task Decrypt_BlobEncryptedWithPreviousKey_StillReadable()
    {
        // v1 шифровал, пока был активным...
        var v1Ring = new EncryptionKeyRing(new EncryptionOptions { MasterKey = Key, ActiveKeyId = "v1" });
        using var encrypted = new MemoryStream();
        await new AesGcmFileCipher(v1Ring).EncryptAsync(new MemoryStream("данные"u8.ToArray()), encrypted);
        encrypted.Position = 0;

        // ...после ротации v1 отставной, активен v2 — блоб по-прежнему расшифровывается.
        var otherKey = Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray());
        var rotatedRing = new EncryptionKeyRing(new EncryptionOptions
        {
            MasterKey = otherKey,
            ActiveKeyId = "v2",
            PreviousKeys = [new EncryptionKeyEntry { Id = "v1", Material = Key }],
        });

        await using var decrypted = await new AesGcmFileCipher(rotatedRing).DecryptAsync(encrypted);
        using var result = new MemoryStream();
        await decrypted.CopyToAsync(result);
        Encoding.UTF8.GetString(result.ToArray()).Should().Be("данные");
    }
}
