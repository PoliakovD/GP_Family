namespace FamilyHub.Api.Features.Auth;

/// <summary>
/// Грубая расшифровка User-Agent для списка «мои устройства» (вкладка «Безопасность») — не
/// полноценный UA-парсер (незачем тянуть зависимость ради этого), просто browser + OS по
/// первому совпадению. UserSession.DeviceInfo пишется как есть при выпуске/ротации токена
/// (см. TokenService.CreateSessionAsync) — парсинг делаем здесь, в момент отдачи наружу.
/// </summary>
public static class UserAgentSummary
{
    public static string Describe(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return "Неизвестное устройство";

        var browser = userAgent switch
        {
            _ when userAgent.Contains("Edg/") => "Edge",
            _ when userAgent.Contains("OPR/") || userAgent.Contains("Opera") => "Opera",
            _ when userAgent.Contains("YaBrowser") => "Яндекс.Браузер",
            _ when userAgent.Contains("Chrome/") => "Chrome",
            _ when userAgent.Contains("CriOS") => "Chrome",
            _ when userAgent.Contains("FxiOS") || userAgent.Contains("Firefox/") => "Firefox",
            _ when userAgent.Contains("Safari/") && userAgent.Contains("Version/") => "Safari",
            _ => null,
        };

        var os = userAgent switch
        {
            _ when userAgent.Contains("iPhone") => "iPhone",
            _ when userAgent.Contains("iPad") => "iPad",
            _ when userAgent.Contains("Android") => "Android",
            _ when userAgent.Contains("Windows") => "Windows",
            _ when userAgent.Contains("Macintosh") || userAgent.Contains("Mac OS X") => "macOS",
            _ when userAgent.Contains("Linux") => "Linux",
            _ => null,
        };

        return (browser, os) switch
        {
            (not null, not null) => $"{browser} · {os}",
            (not null, null) => browser,
            (null, not null) => os,
            _ => "Неизвестное устройство",
        };
    }
}
