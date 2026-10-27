using FamilyHub.Api.Configuration;
using FamilyHub.Api.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FamilyHub.UnitTests.Api.Security;

/// <summary>Регрессия на находку M3 (docs/security/security-audit-2026-10.md): перебор пароля
/// админ-панели блокируется глобально, а не только rate-limit'ом по IP.</summary>
public class AdminLoginThrottleTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly ManualTimeProvider _time = new();

    private AdminLoginThrottle CreateSut(int maxFailed = 3, int lockoutMinutes = 15) => new(
        Options.Create(new AdminOptions { MaxFailedLogins = maxFailed, LockoutDuration = TimeSpan.FromMinutes(lockoutMinutes) }),
        _time);

    [Fact]
    public void ReachingMaxFailures_LocksUntilLockoutEnds()
    {
        var sut = CreateSut();

        sut.RegisterFailure().Should().BeFalse();
        sut.RegisterFailure().Should().BeFalse();
        sut.IsLocked().Should().BeFalse();

        sut.RegisterFailure().Should().BeTrue();
        sut.IsLocked().Should().BeTrue();

        _time.Now = _time.Now.AddMinutes(14);
        sut.IsLocked().Should().BeTrue();

        _time.Now = _time.Now.AddMinutes(2);
        sut.IsLocked().Should().BeFalse();
    }

    [Fact]
    public void FailuresOutsideWindow_DoNotAccumulate()
    {
        var sut = CreateSut();

        sut.RegisterFailure();
        sut.RegisterFailure();
        _time.Now = _time.Now.AddMinutes(16);

        sut.RegisterFailure().Should().BeFalse();
        sut.IsLocked().Should().BeFalse();
    }

    [Fact]
    public void Success_ResetsCounter()
    {
        var sut = CreateSut();

        sut.RegisterFailure();
        sut.RegisterFailure();
        sut.RegisterSuccess();

        sut.RegisterFailure().Should().BeFalse();
        sut.RegisterFailure().Should().BeFalse();
        sut.IsLocked().Should().BeFalse();
    }
}
