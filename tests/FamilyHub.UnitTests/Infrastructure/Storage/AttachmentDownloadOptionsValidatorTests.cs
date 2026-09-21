using FamilyHub.Infrastructure.Storage;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Storage;

/// <summary>Cleanup-рефакторинг (фаза "валидация Options") — см. class doc MinioOptionsValidatorTests,
/// тот же приём для AttachmentDownloadOptions.</summary>
public class AttachmentDownloadOptionsValidatorTests
{
    private readonly AttachmentDownloadOptionsValidator _sut = new();

    [Fact]
    public void Validate_KeySet_Succeeds()
    {
        var result = _sut.Validate(null, new AttachmentDownloadOptions { DownloadSigningKey = "some-key" });

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_KeyMissing_Fails(string? key)
    {
        var result = _sut.Validate(null, new AttachmentDownloadOptions { DownloadSigningKey = key ?? string.Empty });

        result.Failed.Should().BeTrue();
    }
}
