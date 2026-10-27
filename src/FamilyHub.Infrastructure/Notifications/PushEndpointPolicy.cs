namespace FamilyHub.Infrastructure.Notifications;

/// <summary>
/// Какие Web Push-подписки сервер готов принять и использовать (аудит security-audit-2026-10, M1).
/// Endpoint подписки присылает клиент, а WebPushNotificationSender потом сам делает на него POST —
/// без проверки это слепой SSRF во внутреннюю сеть (seq/minio/gotenberg/LM Studio за WireGuard).
/// Поэтому принимаются только https-адреса push-релеев браузеров из allow-list ADR-0004.
/// </summary>
public static class PushEndpointPolicy
{
    public const int MaxEndpointLength = 2048;
    public const int MaxKeyLength = 256;

    private static readonly string[] ExactHosts =
    [
        "fcm.googleapis.com",
        "updates.push.services.mozilla.com",
        "web.push.apple.com",
    ];

    // FCM использует поддомены googleapis.com, WNS (Edge на Windows) — региональные *.notify.windows.com.
    private static readonly string[] HostSuffixes =
    [
        ".googleapis.com",
        ".notify.windows.com",
    ];

    public static bool IsAllowedEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > MaxEndpointLength)
            return false;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
            return false;
        if (uri.HostNameType != UriHostNameType.Dns)
            return false;

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        return ExactHosts.Contains(host) || HostSuffixes.Any(host.EndsWith);
    }

    /// <summary>Ключи подписки (p256dh/auth) — base64url фиксированной небольшой длины.</summary>
    public static bool IsValidKey(string? key) =>
        !string.IsNullOrEmpty(key)
        && key.Length <= MaxKeyLength
        && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '=' or '+' or '/');
}
