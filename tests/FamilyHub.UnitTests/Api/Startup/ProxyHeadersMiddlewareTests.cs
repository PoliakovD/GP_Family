using System.Net;
using FamilyHub.Api.Startup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FamilyHub.UnitTests.Api.Startup;

/// <summary>Регрессия на находку L3 (docs/security/security-audit-2026-10.md).</summary>
public class ProxyHeadersMiddlewareTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void KnownProxiesConfigured_TrustsOnlyThem()
    {
        var options = ProxyHeadersMiddleware.BuildOptions(Config(("ReverseProxy:KnownProxies:0", "172.31.250.2")));

        options.KnownProxies.Should().ContainSingle().Which.Should().Be(IPAddress.Parse("172.31.250.2"));
        options.KnownIPNetworks.Should().BeEmpty("иначе любой контейнер docker-сети подделывал бы X-Forwarded-For");
    }

    [Fact]
    public void NothingConfigured_FallsBackToDockerBridgeRange()
    {
        var options = ProxyHeadersMiddleware.BuildOptions(Config());

        options.KnownIPNetworks.Should().Contain(n => n.BaseAddress.Equals(IPAddress.Parse("172.16.0.0")) && n.PrefixLength == 12);
    }
}
