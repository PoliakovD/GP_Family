namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — заголовки безопасности (аудит
/// module-review-2026-08-02/08-web-frontend-angular.md, находка 2 / 09-config-deployment-devops.md,
/// находка 3), без изменения поведения/порядка.
/// </summary>
public static class SecurityHeadersMiddleware
{
    /// <summary>Раньше не выставлялись нигде — ни на уровне бэкенда (сам раздаёт SPA-сборку через
    /// UseStaticFiles/MapFallbackToFile, отдельного реверс-прокси в репозитории нет), ни в
    /// index.html. CSP — базовый, под реальные нужды текущего фронтенда (без внешних CDN —
    /// шрифты/иконки забандлены, единственный внешний script-src — телеграмовский SDK, подгружаемый
    /// условно, см. index.html): style-src требует 'unsafe-inline' — Angular без CSP-nonce вставляет
    /// component-стили инлайново, это стандартное и ожидаемое ограничение, не наша недоработка.</summary>
    public static WebApplication UseFamilyHubSecurityHeaders(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            // /hangfire (дашборд), /swagger и /dev/* — служебные инструменты за DevTools-флагами (см.
            // DevToolsOptions), у Hangfire.Dashboard и Swagger UI есть собственные инлайн-скрипты без
            // CSP-нонсов — не наш фронтенд, не часть этой находки, не блокируем.
            if (context.Request.Path.StartsWithSegments("/hangfire")
                || context.Request.Path.StartsWithSegments("/swagger")
                || context.Request.Path.StartsWithSegments("/dev"))
            {
                await next();
                return;
            }

            var headers = context.Response.Headers;
            headers["Content-Security-Policy"] =
                "default-src 'self'; " +
                "script-src 'self' https://telegram.org; " +
                "style-src 'self' 'unsafe-inline'; " +
                // data:/blob: — pdf.js рисует страницы через blob:-URL и грузит свой воркер тем же
                // способом (просмотрщик вложений, Анализы/Врачи); worker-src отдельно от script-src,
                // потому что часть браузеров не считает воркеры покрытыми script-src без явного worker-src.
                "img-src 'self' data: blob:; " +
                "worker-src 'self' blob:; " +
                "font-src 'self'; " +
                "connect-src 'self'; " +
                "object-src 'none'; " +
                "frame-ancestors 'none'; " +
                "base-uri 'self'; " +
                "form-action 'self'";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            // X-Frame-Options — тот же запрет, что frame-ancestors выше, для браузеров без поддержки CSP3.
            headers["X-Frame-Options"] = "DENY";
            // HSTS (аудит 09-config-deployment-devops.md, находка 6 — была отложена до решения по
            // реверс-прокси; решение принято, это Caddy с автоматическим TLS). IsHttps здесь уже
            // учитывает X-Forwarded-Proto благодаря UseForwardedHeaders выше — без него это условие
            // никогда не срабатывало бы за прокси, т.к. Kestrel всегда видит голый HTTP от Caddy.
            if (context.Request.IsHttps)
                headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            await next();
        });

        return app;
    }
}
