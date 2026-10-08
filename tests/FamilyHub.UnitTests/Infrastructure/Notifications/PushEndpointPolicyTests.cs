using FamilyHub.Infrastructure.Notifications;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Notifications;

/// <summary>Регрессия на находку M1 (docs/security/security-audit-2026-10.md): endpoint push-подписки —
/// адрес, на который сервер сам делает POST, поэтому принимаются только push-релеи браузеров.</summary>
public class PushEndpointPolicyTests
{
    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc:def")]
    [InlineData("https://android.googleapis.com/gcm/send/abc")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/gAAAA")]
    [InlineData("https://web.push.apple.com/QGx2ZXJ5LWxvbmctdG9rZW4")]
    [InlineData("https://wns2-par02p.notify.windows.com/w/?token=BQYAAA")]
    [InlineData("https://FCM.GoogleApis.com/fcm/send/abc")]
    public void IsAllowedEndpoint_BrowserPushRelays_Allowed(string endpoint) =>
        PushEndpointPolicy.IsAllowedEndpoint(endpoint).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc")]
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/abc")]
    [InlineData("https://user:pass@fcm.googleapis.com/fcm/send/abc")]
    [InlineData("http://seq/api/events/raw")]
    [InlineData("https://minio:9000/bucket")]
    [InlineData("https://localhost/push")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://10.8.0.2:1234/v1/models")]
    [InlineData("https://[::1]/push")]
    [InlineData("https://googleapis.com.evil.example/fcm")]
    [InlineData("https://evilgoogleapis.com/fcm")]
    [InlineData("https://push.example/abc")]
    [InlineData("file:///etc/passwd")]
    public void IsAllowedEndpoint_AnythingElse_Rejected(string? endpoint) =>
        PushEndpointPolicy.IsAllowedEndpoint(endpoint).Should().BeFalse();

    [Fact]
    public void IsAllowedEndpoint_TooLong_Rejected() =>
        PushEndpointPolicy.IsAllowedEndpoint("https://fcm.googleapis.com/" + new string('a', PushEndpointPolicy.MaxEndpointLength))
            .Should().BeFalse();

    [Theory]
    [InlineData("BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUbVlUls0VJXg7A8u-Ts1XbjhazAkj7I99e8QcYP7DkM", true)]
    [InlineData("tBHItJI5svbpez7KI4CCXg", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("key with spaces", false)]
    [InlineData("<script>", false)]
    public void IsValidKey(string? key, bool expected) =>
        PushEndpointPolicy.IsValidKey(key).Should().Be(expected);
}
