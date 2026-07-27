using FamilyHub.Infrastructure.Security;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Security;

public class PasswordHasherTests
{
    [Fact]
    public void HashAndVerify_RoundTrips()
    {
        var hash = PasswordHasher.Hash("Passw0rd");

        hash.Should().StartWith("pbkdf2:210000:");
        PasswordHasher.Verify("Passw0rd", hash).Should().BeTrue();
        PasswordHasher.Verify("Passw0rf", hash).Should().BeFalse();
    }

    [Fact]
    public void Hash_SamePasswordTwice_DiffersBySalt()
    {
        PasswordHasher.Hash("Passw0rd").Should().NotBe(PasswordHasher.Hash("Passw0rd"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("мусор")]
    [InlineData("pbkdf2:abc:x:y")]
    public void Verify_MalformedStoredValue_ReturnsFalse(string stored)
    {
        PasswordHasher.Verify("Passw0rd", stored).Should().BeFalse();
    }

    [Fact]
    public void Verify_LegacyPinFormat_StillWorks()
    {
        // Регрессия: старые 4-8-значные numeric PIN-хеши (до перехода на политику паролей,
        // см. FamilyHub.Domain.ValueObjects.PasswordRules) должны продолжать верифицироваться —
        // формат/алгоритм хеша не менялся, менялось только правило СОЗДАНИЯ нового секрета.
        var legacyHash = PasswordHasher.Hash("1234");

        PasswordHasher.Verify("1234", legacyHash).Should().BeTrue();
    }
}
