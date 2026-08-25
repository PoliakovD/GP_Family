using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FamilyHub.Infrastructure.Auth.Jwt;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Auth.Jwt;

public class JwtSigningKeyRingTests
{
    private const string KeyA = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";
    private const string KeyB = "ZmVkY2JhOTg3NjU0MzIxMGZlZGNiYTk4NzY1NDMyMTA=";

    [Fact]
    public void Build_ActiveKeyFirst_CarriesActiveKeyIdAsKeyId()
    {
        var keys = JwtSigningKeyRing.Build(new FamilyHub.Infrastructure.Auth.Jwt.JwtOptions
        {
            SigningKey = KeyA,
            ActiveKeyId = "v1",
        });

        keys.Should().ContainSingle();
        keys[0].KeyId.Should().Be("v1");
    }

    [Fact]
    public void Build_WithPreviousKeys_IncludesAllWithTheirIds()
    {
        var keys = JwtSigningKeyRing.Build(new FamilyHub.Infrastructure.Auth.Jwt.JwtOptions
        {
            SigningKey = KeyB,
            ActiveKeyId = "v2",
            PreviousSigningKeys = [new JwtKeyEntry { Id = "v1", Material = KeyA }],
        });

        keys.Select(k => k.KeyId).Should().BeEquivalentTo(["v2", "v1"]);
    }

    [Fact]
    public void Build_DuplicateKeyIdBetweenActiveAndPrevious_Throws()
    {
        var act = () => JwtSigningKeyRing.Build(new FamilyHub.Infrastructure.Auth.Jwt.JwtOptions
        {
            SigningKey = KeyA,
            ActiveKeyId = "v1",
            PreviousSigningKeys = [new JwtKeyEntry { Id = "v1", Material = KeyB }],
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*v1*");
    }

    [Fact]
    public void Build_MissingActiveSigningKey_Throws()
    {
        var act = () => JwtSigningKeyRing.Build(new FamilyHub.Infrastructure.Auth.Jwt.JwtOptions { SigningKey = "" });

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Build_InvalidBase64SigningKey_Throws()
    {
        var act = () => JwtSigningKeyRing.Build(new FamilyHub.Infrastructure.Auth.Jwt.JwtOptions { SigningKey = "CHANGE_ME" });

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Build_PreviousKeyEntryWithoutId_Throws()
    {
        var act = () => JwtSigningKeyRing.Build(new FamilyHub.Infrastructure.Auth.Jwt.JwtOptions
        {
            SigningKey = KeyA,
            ActiveKeyId = "v1",
            PreviousSigningKeys = [new JwtKeyEntry { Id = "", Material = KeyB }],
        });

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// Сквозной сценарий ротации (ADR-0009): токен, выпущенный старым активным ключом ДО
    /// ротации, обязан оставаться валидным против связки, где этот ключ уже отставной — иначе
    /// смена Jwt:SigningKey мгновенно разлогинивает всех, у кого на руках живой access-токен.
    /// </summary>
    [Fact]
    public void TokenSignedWithOldActiveKey_ValidatesAgainstRotatedRing()
    {
        var oldRing = JwtSigningKeyRing.Build(new FamilyHub.Infrastructure.Auth.Jwt.JwtOptions
        {
            SigningKey = KeyA,
            ActiveKeyId = "v1",
        });
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "FamilyHub",
            audience: "FamilyHub.Pwa",
            claims: [new Claim("sub", "test-user")],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(oldRing[0], SecurityAlgorithms.HmacSha256)));

        var rotatedRing = JwtSigningKeyRing.Build(new FamilyHub.Infrastructure.Auth.Jwt.JwtOptions
        {
            SigningKey = KeyB,
            ActiveKeyId = "v2",
            PreviousSigningKeys = [new JwtKeyEntry { Id = "v1", Material = KeyA }],
        });

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "FamilyHub",
            ValidateAudience = true,
            ValidAudience = "FamilyHub.Pwa",
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = rotatedRing,
            ValidateLifetime = true,
        };

        var act = () => new JwtSecurityTokenHandler().ValidateToken(token, validationParameters, out _);
        act.Should().NotThrow();
    }

    [Fact]
    public void TokenSignedWithKeyOutsideRing_FailsValidation()
    {
        var unrelatedRing = JwtSigningKeyRing.Build(new FamilyHub.Infrastructure.Auth.Jwt.JwtOptions
        {
            SigningKey = KeyA,
            ActiveKeyId = "v1",
        });
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "FamilyHub",
            audience: "FamilyHub.Pwa",
            claims: [new Claim("sub", "test-user")],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(unrelatedRing[0], SecurityAlgorithms.HmacSha256)));

        var ringWithoutThatKey = JwtSigningKeyRing.Build(new FamilyHub.Infrastructure.Auth.Jwt.JwtOptions
        {
            SigningKey = KeyB,
            ActiveKeyId = "v2",
        });

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "FamilyHub",
            ValidateAudience = true,
            ValidAudience = "FamilyHub.Pwa",
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = ringWithoutThatKey,
            ValidateLifetime = true,
        };

        var act = () => new JwtSecurityTokenHandler().ValidateToken(token, validationParameters, out _);
        act.Should().Throw<SecurityTokenException>();
    }
}
