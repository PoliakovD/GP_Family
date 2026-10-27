using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>AdminWebFactory + Admin:AllowedHosts — как на проде (admin.{PUBLIC_DOMAIN}:4059).</summary>
public class AdminHostGuardWebFactory : AdminWebFactory
{
    public const string AdminHost = "admin.test.local:4059";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Admin:AllowedHosts:0", AdminHost);
    }
}

[CollectionDefinition(Name)]
public class AdminHostGuardCollection : ICollectionFixture<AdminHostGuardWebFactory>
{
    public const string Name = "FamilyHub admin host guard tests";
}

/// <summary>
/// Регрессия на находку M3 (docs/security/security-audit-2026-10.md): админ-панель обслуживается
/// только на своём Host — на публичном домене она не существует, даже если Caddy её пропустит.
/// </summary>
[Collection(AdminHostGuardCollection.Name)]
public class AdminHostGuardApiTests(AdminHostGuardWebFactory factory)
{
    private static HttpRequestMessage Login(string host) =>
        new(HttpMethod.Post, "/api/admin/session")
        {
            Headers = { Host = host },
            Content = JsonContent.Create(new { user = AdminWebFactory.TestUser, password = AdminWebFactory.TestPassword }),
        };

    [Theory]
    [InlineData("gp-family.example")]
    [InlineData("admin.test.local:8443")]
    [InlineData("admin.test.local")]
    public async Task AdminApi_OnForeignHost_Returns404(string host)
    {
        var client = factory.CreateClient();

        (await client.SendAsync(Login(host))).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var spa = new HttpRequestMessage(HttpMethod.Get, "/admin/login") { Headers = { Host = host } };
        (await client.SendAsync(spa)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AdminApi_OnAdminHost_Works()
    {
        var client = factory.CreateClient();

        (await client.SendAsync(Login(AdminHostGuardWebFactory.AdminHost))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OrdinaryApi_IsNotAffectedByHost()
    {
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/families") { Headers = { Host = "gp-family.example" } };

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
