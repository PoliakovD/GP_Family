using FamilyHub.Infrastructure.Authorization;
using Serilog;
using Serilog.Events;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — структурированное логирование HTTP-запросов
/// в Seq, без изменения поведения/порядка.
/// </summary>
public static class RequestLoggingRegistration
{
    /// <summary>Уровень поднимается на 4xx/5xx и падает на Debug для успешных запросов к
    /// статике/Hangfire/health — иначе Seq захлёстывает шумом от каждого ассета SPA и от
    /// healthcheck-поллинга раз в несколько секунд.</summary>
    public static WebApplication UseFamilyHubRequestLogging(this WebApplication app)
    {
        app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} -> {StatusCode} за {Elapsed:0.0}мс";

            options.GetLevel = (httpContext, elapsed, ex) => ex is not null
                ? LogEventLevel.Error
                : httpContext.Response.StatusCode >= 500 ? LogEventLevel.Error
                : httpContext.Response.StatusCode >= 400 ? LogEventLevel.Warning
                : httpContext.Request.Path.StartsWithSegments("/hangfire")
                    || httpContext.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Debug
                : LogEventLevel.Information;

            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                diagnosticContext.Set("RemoteIp", httpContext.Connection.RemoteIpAddress?.ToString());
                diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString());
                diagnosticContext.Set("QueryString", httpContext.Request.QueryString.Value);

                var userId = httpContext.User.FindFirst(FamilyHubClaimTypes.UserId)?.Value;
                if (userId is not null)
                    diagnosticContext.Set("UserId", userId);
            };
        });

        return app;
    }
}
