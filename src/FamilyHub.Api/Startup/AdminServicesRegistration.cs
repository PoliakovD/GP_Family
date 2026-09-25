using FamilyHub.Api.Features.Admin;
using FamilyHub.Infrastructure.Security.Credentials;

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

        // Ротация учёток приложения к Postgres/MinIO (ADR-0011).
        builder.Services.AddScoped<IDbCredentialAdmin, NpgsqlDbCredentialAdmin>();
        // Таймаут короче стандартного (100 с): admin API MinIO отвечает за миллисекунды, а панель не
        // должна висеть, если хранилище не отвечает.
        builder.Services.AddHttpClient<IMinioCredentialAdmin, MinioAdminClient>(c => c.Timeout = TimeSpan.FromSeconds(20));
        builder.Services.AddScoped<AdminCredentialsService>();
        builder.Services.AddScoped<AdminKbRebuildService>();
        builder.Services.AddScoped<AdminAttentionService>();
        builder.Services.AddScoped<AdminSearchWarmupService>();

        return builder;
    }
}
