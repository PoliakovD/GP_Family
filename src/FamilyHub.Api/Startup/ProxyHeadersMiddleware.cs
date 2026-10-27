using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — заголовки от Caddy (реверс-прокси,
/// деплой-план), без изменения поведения/порядка.
/// </summary>
public static class ProxyHeadersMiddleware
{
    public const string KnownProxiesSection = "ReverseProxy:KnownProxies";

    /// <summary>Без этого Request.Scheme всегда "http" — secure-куки (JWT access-cookie, CSRF)
    /// выставлялись бы без Secure, а RemoteIpAddress у ВСЕХ запросов стал бы адресом Caddy (ломает
    /// партиционирование rate limiter по IP и RemoteIp в логах Seq).</summary>
    public static WebApplication UseFamilyHubProxyHeaders(this WebApplication app)
    {
        app.UseForwardedHeaders(BuildOptions(app.Configuration));
        return app;
    }

    /// <summary>
    /// ReverseProxy:KnownProxies задан (прод: фиксированный IP Caddy в сети edge) — X-Forwarded-*
    /// принимаются только от этих адресов (аудит security-audit-2026-10, L3): раньше доверялась вся
    /// 172.16.0.0/12, и любой контейнер docker-сети подделывал IP клиента (обход rate-limit по IP,
    /// ложный RemoteIp в аудит-логах) и схему. Не задан (дев/e2e — IP прокси не фиксирован) — прежнее
    /// поведение: весь диапазон docker-мостов по умолчанию, а не «доверяю всем».
    /// </summary>
    public static ForwardedHeadersOptions BuildOptions(IConfiguration configuration)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        };
        var proxies = configuration.GetSection(KnownProxiesSection).Get<string[]>()?
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => IPAddress.Parse(p.Trim()))
            .ToArray() ?? [];

        if (proxies.Length > 0)
        {
            // Дефолтные loopback-сеть/прокси ASP.NET Core тоже сбрасываем: доверяем только явно указанным.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var proxy in proxies) options.KnownProxies.Add(proxy);
        }
        else
        {
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
        }

        return options;
    }
}
