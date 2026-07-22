using FamilyHub.Infrastructure.Security;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Security;

public class PinHasherTests
{
    [Fact]
    public void HashAndVerify_RoundTrips()
    {
        var hash = PinHasher.Hash("1234");

        hash.Should().StartWith("pbkdf2:210000:");
        PinHasher.Verify("1234", hash).Should().BeTrue();
        PinHasher.Verify("1235", hash).Should().BeFalse();
    }

    [Fact]
    public void Hash_SamePinTwice_DiffersBySalt()
    {
        PinHasher.Hash("1234").Should().NotBe(PinHasher.Hash("1234"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("мусор")]
    [InlineData("pbkdf2:abc:x:y")]
    public void Verify_MalformedStoredValue_ReturnsFalse(string stored)
    {
        PinHasher.Verify("1234", stored).Should().BeFalse();
    }
}
