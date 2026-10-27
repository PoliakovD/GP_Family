using FamilyHub.Api.Configuration;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Админ-панель (/api/admin/*, SPA-маршруты /admin*) обслуживается только на своих Host-заголовках
/// из Admin:AllowedHosts (аудит security-audit-2026-10, M3). Раньше её отделял от публичного домена
/// лишь путевой фильтр Caddy: любая ошибка в Caddyfile или новый маршрут вне шаблона открывали форму
/// входа (и ротацию учёток БД/MinIO за ней) в интернет. На чужом Host отвечаем 404 — до
/// аутентификации и до SPA-fallback, чтобы панель на публичном домене выглядела несуществующей.
/// </summary>
public static class AdminHostGuardMiddleware
{
    public static WebApplication UseFamilyHubAdminHostGuard(this WebApplication app, AdminOptions admin)
    {
        if (!admin.Enabled) return app;

        var allowedHosts = admin.AllowedHosts.Where(h => !string.IsNullOrWhiteSpace(h)).ToArray();
        if (allowedHosts.Length == 0)
        {
            app.Logger.LogWarning(
                "Admin:AllowedHosts не задан — админ-панель отвечает на любом Host. На проде задайте " +
                "Admin__AllowedHosts__0=admin.<домен>:4059 (аудит security-audit-2026-10, M3).");
            return app;
        }

        app.Use(async (context, next) =>
        {
            if (IsAdminPath(context.Request.Path) && !IsAllowedHost(context.Request.Host, allowedHosts))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });

        return app;
    }

    public static bool IsAdminPath(PathString path) =>
        path.StartsWithSegments("/api/admin", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase);

    /// <summary>Сравнение без учёта регистра, порт — как в заголовке (admin.example.ru:4059).</summary>
    public static bool IsAllowedHost(HostString host, IReadOnlyCollection<string> allowedHosts) =>
        host.HasValue && allowedHosts.Any(h => string.Equals(h.Trim(), host.Value, StringComparison.OrdinalIgnoreCase));
}
