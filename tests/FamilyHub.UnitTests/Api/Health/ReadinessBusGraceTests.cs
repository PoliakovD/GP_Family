using FamilyHub.Api.Startup;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace FamilyHub.UnitTests.Api.Health;

/// <summary>/health/ready и шина MassTransit: шина стартует позже Kestrel, поэтому первые
/// BusStartupGrace её проверка не требуется, дальше — требуется (зависшая шина = честный 503).</summary>
public class ReadinessBusGraceTests
{
    private static HealthCheckRegistration Check(string name, params string[] tags) =>
        new(name, _ => null!, HealthStatus.Unhealthy, tags);

    [Fact]
    public void Bus_IsSkipped_DuringStartupGrace()
    {
        var bus = Check(HealthChecksRegistration.BusCheckName, "ready", "masstransit");

        HealthChecksRegistration.IsReadyCheck(bus, TimeSpan.FromSeconds(10)).Should().BeFalse();
    }

    [Fact]
    public void Bus_IsRequired_AfterStartupGrace()
    {
        var bus = Check(HealthChecksRegistration.BusCheckName, "ready", "masstransit");

        HealthChecksRegistration.IsReadyCheck(bus, HealthChecksRegistration.BusStartupGrace + TimeSpan.FromSeconds(1))
            .Should().BeTrue("шина, так и не стартовавшая за окно прогрева, должна валить готовность");
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("minio")]
    [InlineData("kafka")]
    public void OtherReadyChecks_AreAlwaysRequired_EvenDuringGrace(string name)
    {
        HealthChecksRegistration.IsReadyCheck(Check(name, "ready"), TimeSpan.Zero).Should().BeTrue();
    }

    [Fact]
    public void ChecksWithoutReadyTag_AreNeverIncluded()
    {
        HealthChecksRegistration.IsReadyCheck(Check("llm", "llm"), TimeSpan.FromHours(1)).Should().BeFalse();
    }
}
