using FamilyHub.Api.Startup;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Api.Startup;

/// <summary>Регрессия на находку L1 (docs/security/security-audit-2026-10.md): секреты из URL не попадают в логи.</summary>
public class LogPathMaskerTests
{
    [Theory]
    [InlineData("/api/public/doctor-reports/AbC_123-xyz", "/api/public/doctor-reports/***")]
    [InlineData("/api/public/doctor-reports/AbC_123-xyz/pdf", "/api/public/doctor-reports/***/pdf")]
    [InlineData("/api/public/dose-actions/tok3n", "/api/public/dose-actions/***")]
    [InlineData("/api/invites/0123abcd/redeem", "/api/invites/***/redeem")]
    [InlineData("/api/invites/0123abcd/preview", "/api/invites/***/preview")]
    [InlineData("/r/report-token", "/r/***")]
    [InlineData("/join/invite-code", "/join/***")]
    [InlineData("/API/Public/Doctor-Reports/AbC", "/api/public/doctor-reports/***")]
    public void MaskPath_SecretSegments_Masked(string path, string expected) =>
        LogPathMasker.MaskPath(path).Should().Be(expected);

    [Theory]
    [InlineData("/api/families")]
    [InlineData("/api/doctor-reports/0b1a6f8e-0000-0000-0000-000000000000/pdf")]
    [InlineData("/api/public/doctor-reports/")]
    [InlineData("/report")]
    [InlineData("/")]
    [InlineData("")]
    public void MaskPath_OtherPaths_Unchanged(string path) =>
        LogPathMasker.MaskPath(path).Should().Be(path);

    [Theory]
    [InlineData("?expires=1760000000&sig=deadbeef", "?expires=1760000000&sig=***")]
    [InlineData("?sig=deadbeef&expires=1", "?sig=***&expires=1")]
    [InlineData("?token=abc&x=1", "?token=***&x=1")]
    [InlineData("?code=123456", "?code=***")]
    [InlineData("?signature=keep&barcode=keep", "?signature=keep&barcode=keep")]
    [InlineData("?page=2", "?page=2")]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void MaskQuery(string? query, string? expected) =>
        LogPathMasker.MaskQuery(query).Should().Be(expected);
}
