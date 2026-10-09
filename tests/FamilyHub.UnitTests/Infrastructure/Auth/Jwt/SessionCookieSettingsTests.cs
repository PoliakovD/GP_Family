using FamilyHub.Infrastructure.Auth.Jwt;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Auth.Jwt;

/// <summary>__Host- cookie на проде (docs/security/security-audit-2026-10.md, бэклог M4).</summary>
public class SessionCookieSettingsTests
{
    [Fact]
    public void HostPrefixed_PrefixesNames_RootPath_AlwaysSecure()
    {
        var sut = new SessionCookieSettings(hostPrefixed: true);
        var http = new DefaultHttpContext();
        http.Request.Scheme = "http";

        sut.AccessToken.Should().Be("__Host-familyhub.at");
        sut.RefreshToken.Should().Be("__Host-familyhub.rt");
        sut.Antiforgery.Should().Be("__Host-familyhub.csrf");
        sut.AdminSession.Should().Be("__Host-familyhub.admin");
        sut.RefreshTokenPath.Should().Be("/", "браузер отвергает __Host- cookie с другим Path");
        sut.Secure(http).Should().BeTrue("браузер отвергает __Host- cookie без Secure");
    }

    [Fact]
    public void NotPrefixed_KeepsLegacyNames_AndSecureFollowsRequest()
    {
        var sut = new SessionCookieSettings(hostPrefixed: false);
        var http = new DefaultHttpContext();

        sut.AccessToken.Should().Be(PwaCookieNames.AccessToken);
        sut.RefreshToken.Should().Be(PwaCookieNames.RefreshToken);
        sut.RefreshTokenPath.Should().Be("/api/auth");

        http.Request.Scheme = "http";
        sut.Secure(http).Should().BeFalse();
        http.Request.Scheme = "https";
        sut.Secure(http).Should().BeTrue();
    }
}
