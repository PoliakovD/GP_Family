using FamilyHub.Api.Features.Admin;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге. В исходном файле эти пять регистраций были
/// вклинены ПРЯМО ВНУТРЬ настройки auth-схем (между AddScheme&lt;AdminAuthenticationHandler&gt; и
/// AddPolicyScheme(Smart)) — по смыслу это Core-сервисы админки, не аутентификация; вынесены сюда
/// как отдельный, ясно поименованный шаг.
/// </summary>
public static class AdminServicesRegistration
{
    public static WebApplicationBuilder AddFamilyHubAdminServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<AdminStatsService>();
        builder.Services.AddScoped<AdminKeysService>();
        builder.Services.AddScoped<AdminConfigService>();
        builder.Services.AddScoped<AdminKbRebuildService>();
        builder.Services.AddScoped<AdminAttentionService>();
        builder.Services.AddScoped<AdminSearchWarmupService>();

        return builder;
    }
}
