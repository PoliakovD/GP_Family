using FamilyHub.Api.Startup;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace FamilyHub.UnitTests.Api.Startup;

/// <summary>Регрессия на находку M3 (docs/security/security-audit-2026-10.md).</summary>
public class AdminHostGuardMiddlewareTests
{
    private static readonly string[] Allowed = ["admin.gp-family.ru:4059"];

    [Theory]
    [InlineData("admin.gp-family.ru:4059", true)]
    [InlineData("ADMIN.gp-family.ru:4059", true)]
    [InlineData("admin.gp-family.ru:8443", false)]
    [InlineData("admin.gp-family.ru", false)]
    [InlineData("gp-family.ru", false)]
    [InlineData("", false)]
    public void IsAllowedHost(string host, bool expected) =>
        AdminHostGuardMiddleware.IsAllowedHost(new HostString(host), Allowed).Should().Be(expected);

    [Theory]
    [InlineData("/api/admin/session", true)]
    [InlineData("/API/Admin/stats", true)]
    [InlineData("/admin", true)]
    [InlineData("/admin/login", true)]
    [InlineData("/api/families", false)]
    [InlineData("/administrator", false)]
    [InlineData("/health/ready", false)]
    public void IsAdminPath(string path, bool expected) =>
        AdminHostGuardMiddleware.IsAdminPath(new PathString(path)).Should().Be(expected);
}
