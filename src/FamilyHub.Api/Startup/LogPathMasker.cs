using System.Text.RegularExpressions;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Убирает из логируемых путей и query-строк секреты, которые передаются прямо в URL (аудит
/// security-audit-2026-10, L1): токены публичных отчётов для врача и действий с дозой из push,
/// коды приглашений и HMAC-подписи ссылок на вложения. Всё это — capability-ссылки: кто прочитал
/// лог в Seq (у оператора) в пределах их срока жизни, тот получил доступ к данным.
/// </summary>
public static partial class LogPathMasker
{
    public const string Mask = "***";

    // Сегмент сразу после этих префиксов — секрет. /r/ и /join/ — SPA-маршруты, но запрос за
    // index.html с таким путём тоже логируется.
    private static readonly string[] SecretSegmentPrefixes =
    [
        "/api/public/doctor-reports/",
        "/api/public/dose-actions/",
        "/api/invites/",
        "/r/",
        "/join/",
    ];

    public static string MaskPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path ?? string.Empty;

        foreach (var prefix in SecretSegmentPrefixes)
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || path.Length == prefix.Length) continue;

            var end = path.IndexOf('/', prefix.Length);
            return end < 0 ? prefix + Mask : prefix + Mask + path[end..];
        }
        return path;
    }

    public static string? MaskQuery(string? query) =>
        string.IsNullOrEmpty(query) ? query : SecretQueryParam().Replace(query, m => m.Groups["key"].Value + "=" + Mask);

    [GeneratedRegex(@"(?<key>(?<=[?&])(sig|token|code))=[^&]*", RegexOptions.IgnoreCase)]
    private static partial Regex SecretQueryParam();
}
