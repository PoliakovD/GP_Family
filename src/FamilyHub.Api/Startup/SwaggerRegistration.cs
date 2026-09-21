using FamilyHub.Api.Configuration;
using FamilyHub.Api.Security;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — Swagger (ручное тестирование), без изменения
/// поведения/порядка. Add- и Use-часть в одном файле — маленькая цельная фича, обе стороны нужны
/// только друг с другом.
/// </summary>
public static class SwaggerRegistration
{
    public static WebApplicationBuilder AddFamilyHubSwagger(this WebApplicationBuilder builder)
    {
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        return builder;
    }

    /// <summary>Раньше только Development, теперь DevTools:AdminUiEnabled (см. DevToolsOptions):
    /// на VPS доступен за тем же BasicAuth, что и Hangfire-дашборд, поверх периметра (Caddy пускает
    /// /swagger только на WireGuard-адресе).</summary>
    public static WebApplication UseFamilyHubSwagger(this WebApplication app, DevToolsOptions devTools)
    {
        if (!devTools.AdminUiEnabled) return app;

        app.UseWhen(
            context => context.Request.Path.StartsWithSegments("/swagger"),
            branch => branch.Use(async (context, next) =>
            {
                if (!AdminBasicAuth.IsAuthorized(context, devTools))
                {
                    AdminBasicAuth.Challenge(context);
                    return;
                }
                await next();
            }));
        app.UseSwagger();
        app.UseSwaggerUI();

        return app;
    }
}
