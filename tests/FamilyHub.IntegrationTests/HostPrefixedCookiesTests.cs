using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>Как на проде: Auth:HostPrefixedCookies=true, клиент по https.</summary>
public class HostPrefixedCookiesWebFactory : FamilyHubWebFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Auth:HostPrefixedCookies", "true");
    }
}

[CollectionDefinition(Name)]
public class HostPrefixedCookiesCollection : ICollectionFixture<HostPrefixedCookiesWebFactory>
{
    public const string Name = "FamilyHub __Host- cookies";
}

/// <summary>
/// Регрессия к бэклогу M4 (docs/security/security-audit-2026-10.md): сессионные cookie PWA с префиксом
/// __Host- (Secure, Path=/, без Domain) — их нельзя подбросить с поддомена — и сессия с ними работает.
/// </summary>
[Collection(HostPrefixedCookiesCollection.Name)]
public class HostPrefixedCookiesTests(HostPrefixedCookiesWebFactory factory)
{
    [Fact]
    public async Task Register_IssuesHostPrefixedCookies_AndSessionWorks()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var email = $"host-{Guid.NewGuid():N}@example.com";

        (await client.PostAsJsonAsync("/api/auth/register/start", new { email })).StatusCode.Should().Be(HttpStatusCode.OK);
        var confirm = await client.PostAsJsonAsync("/api/auth/register/confirm", new
        {
            email, code = factory.Emails.LastCodeFor(email), password = "Passw0rd",
            username = $"host{Guid.NewGuid():N}"[..20], lastName = "Префиксов", firstName = "Хост",
            middleName = (string?)null, birthDate = new DateOnly(1990, 1, 1), gender = 0,
        });
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);

        var setCookies = confirm.Headers.GetValues("Set-Cookie").ToList();
        foreach (var name in new[] { "__Host-familyhub.at", "__Host-familyhub.rt" })
        {
            var header = setCookies.Should().ContainSingle(h => h.StartsWith(name + "=", StringComparison.Ordinal)).Subject;
            header.Should().ContainEquivalentOf("secure").And.ContainEquivalentOf("path=/;").And.NotContainEquivalentOf("domain=");
        }
        setCookies.Should().NotContain(h => h.StartsWith("familyhub.at=", StringComparison.Ordinal));

        (await client.GetAsync("/api/families")).StatusCode.Should().Be(HttpStatusCode.OK);
        var me = await client.GetAsync("/api/auth/me");
        me.Headers.GetValues("Set-Cookie").Should().Contain(h => h.StartsWith("__Host-familyhub.csrf=", StringComparison.Ordinal));
        (await client.PostAsync("/api/auth/refresh", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
