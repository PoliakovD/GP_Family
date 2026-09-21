using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — заголовки от Caddy (реверс-прокси,
/// деплой-план), без изменения поведения/порядка.
/// </summary>
public static class ProxyHeadersMiddleware
{
    /// <summary>Без этого Request.Scheme всегда "http" — secure-куки (JWT access-cookie, CSRF)
    /// выставлялись бы без Secure, а RemoteIpAddress у ВСЕХ запросов стал бы адресом Caddy (ломает
    /// партиционирование rate limiter по IP и RemoteIp в логах Seq). KnownNetworks — весь диапазон
    /// docker-мостов по умолчанию (172.16/12), не "доверяю всем" (X-Forwarded-* игнорируются от
    /// источников вне этой сети).</summary>
    public static WebApplication UseFamilyHubProxyHeaders(this WebApplication app)
    {
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            KnownIPNetworks = { new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12) },
        });

        return app;
    }
}
