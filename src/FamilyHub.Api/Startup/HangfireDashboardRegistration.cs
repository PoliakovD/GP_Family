using FamilyHub.Api.Configuration;
using FamilyHub.Api.Security;
using Hangfire;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — дашборд Hangfire, без изменения
/// поведения/порядка.
/// </summary>
public static class HangfireDashboardRegistration
{
    /// <summary>DevTools:AdminUiEnabled (см. DevToolsOptions). Раньше был только Development с
    /// пустым Authorization, что означало анонимный доступ в тот же момент, когда контур
    /// становится Production по среде (дев-контур на VPS) — поэтому здесь собственный
    /// BasicAuth-фильтр, а не голое AllowAnonymous.</summary>
    public static WebApplication MapFamilyHubHangfireDashboard(this WebApplication app, DevToolsOptions devTools)
    {
        if (!devTools.AdminUiEnabled) return app;

        // AllowAnonymous обязателен: FallbackPolicy требует аутентификации для всех эндпоинтов без
        // явного исключения, а у браузера при заходе на /hangfire нет ни Telegram initData, ни
        // dev-заголовка X-Dev-TelegramId — реальная проверка личности здесь теперь
        // HangfireBasicAuthFilter, не ASP.NET Core-аутентификация.
        app.MapHangfireDashboard("/hangfire", new DashboardOptions
        {
            Authorization = [new HangfireBasicAuthFilter(app.Services.GetRequiredService<IOptions<DevToolsOptions>>())],
        }).AllowAnonymous();

        return app;
    }
}
