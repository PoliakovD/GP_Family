using FamilyHub.Infrastructure.Storage;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Storage;

/// <summary>Cleanup-рефакторинг (фаза "валидация Options") — заменяет прежний ручной
/// if/throw-guard в AddFamilyHubFileStorage типизированным IValidateOptions&lt;MinioOptions&gt;,
/// подключённым через AddOptions&lt;MinioOptions&gt;().ValidateOnStart(). Прямые юнит-тесты на сам
/// валидатор — чистая функция, БД/хост не нужны.</summary>
public class MinioOptionsValidatorTests
{
    private readonly MinioOptionsValidator _sut = new();

    [Fact]
    public void Validate_AllRequiredFieldsSet_Succeeds()
    {
        var result = _sut.Validate(null, new MinioOptions
        {
            Endpoint = "localhost:9000", AccessKey = "key", SecretKey = "secret",
        });

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "key", "secret")]
    [InlineData("localhost:9000", "", "secret")]
    [InlineData("localhost:9000", "key", "")]
    [InlineData(null, null, null)]
    public void Validate_MissingRequiredField_Fails(string? endpoint, string? accessKey, string? secretKey)
    {
        var result = _sut.Validate(null, new MinioOptions
        {
            Endpoint = endpoint ?? string.Empty, AccessKey = accessKey ?? string.Empty, SecretKey = secretKey ?? string.Empty,
        });

        result.Failed.Should().BeTrue();
    }
}
