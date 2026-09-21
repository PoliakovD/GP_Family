using FamilyHub.Api.Configuration;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Api.Configuration;

/// <summary>Cleanup-рефакторинг (фаза "валидация Options") — см. class doc MinioOptionsValidatorTests
/// (FamilyHub.UnitTests.Infrastructure.Storage), тот же приём для InternalOptions. В отличие от
/// Minio/AttachmentDownload, BotApiToken опционален — пусто легитимно (см. class doc InternalOptions).</summary>
public class InternalOptionsValidatorTests
{
    private readonly InternalOptionsValidator _sut = new();

    [Fact]
    public void Validate_TokenEmpty_Succeeds_NotConfiguredIsValid()
    {
        var result = _sut.Validate(null, new InternalOptions { BotApiToken = string.Empty });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_TokenAtLeast32Chars_Succeeds()
    {
        var result = _sut.Validate(null, new InternalOptions { BotApiToken = new string('a', 32) });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_TokenShorterThan32Chars_Fails()
    {
        var result = _sut.Validate(null, new InternalOptions { BotApiToken = new string('a', 31) });

        result.Failed.Should().BeTrue();
    }
}
