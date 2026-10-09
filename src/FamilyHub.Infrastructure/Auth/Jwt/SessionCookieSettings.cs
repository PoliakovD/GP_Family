using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyHub.Infrastructure.Auth.Jwt;

/// <summary>
/// Имена и атрибуты сессионных cookie (PWA и админ-панели). При <see cref="HostPrefixed"/>
/// (Auth:HostPrefixedCookies, прод) у них префикс <c>__Host-</c> (аудит security-audit-2026-10, бэклог M4):
/// браузер принимает такую cookie только с Secure, Path=/ и без Domain, поэтому её нельзя ни выставить,
/// ни перезаписать с поддомена (seq./s3./admin. — same-site). Без префикса (дев, интеграционные тесты)
/// всё работает по http, где Secure-cookie браузер/CookieContainer не вернул бы.
///
/// Публичная CSRF-cookie (CsrfCookieNames.PublicToken) префикс не получает: её имя читает Angular
/// одной сборкой для дева и прода, а подменить её бесполезно — токен сверяется с приватной половиной
/// (<see cref="Antiforgery"/>), которая префикс получает.
/// </summary>
public sealed class SessionCookieSettings(bool hostPrefixed)
{
    public const string ConfigKey = "Auth:HostPrefixedCookies";
    private const string HostPrefix = "__Host-";

    public bool HostPrefixed { get; } = hostPrefixed;

    public string AccessToken => Name(PwaCookieNames.AccessToken);

    public string RefreshToken => Name(PwaCookieNames.RefreshToken);

    /// <summary>__Host- требует Path=/; без префикса refresh-cookie ограничена /api/auth (не ездит с каждым запросом).</summary>
    public string RefreshTokenPath => HostPrefixed ? "/" : "/api/auth";

    /// <summary>Приватная (httpOnly) половина антифорджери-токена.</summary>
    public string Antiforgery => Name("familyhub.csrf");

    public string AdminSession => Name("familyhub.admin");

    /// <summary>Secure для cookie этого запроса: с префиксом — всегда (иначе браузер её отвергнет).</summary>
    public bool Secure(HttpContext http) => HostPrefixed || http.Request.IsHttps;

    public static SessionCookieSettings For(HttpContext http) =>
        http.RequestServices.GetRequiredService<SessionCookieSettings>();

    private string Name(string baseName) => HostPrefixed ? HostPrefix + baseName : baseName;
}
