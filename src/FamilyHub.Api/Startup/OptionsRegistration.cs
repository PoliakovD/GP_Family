using FamilyHub.Api.Configuration;
using FamilyHub.Api.Features.Auth;
using FamilyHub.Infrastructure.Auth.Jwt;
using FamilyHub.Infrastructure.Consents;
using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.Email;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Notifications;
using FamilyHub.Infrastructure.Previews;
using FamilyHub.Infrastructure.Security;
using FamilyHub.Infrastructure.Storage;
using FamilyHub.Infrastructure.Telegram;
using FamilyHub.Modules.Medical.Attachments;
using FamilyHub.Modules.Medical.Extraction;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — типизированный биндинг Options-секций,
/// без изменения поведения/порядка. Часть секций (DevTools/Admin) читается синхронно ЗДЕСЬ ЖЕ,
/// т.к. от их значений зависит, какие сервисы вообще регистрировать ниже по Program.cs, до
/// builder.Build() — тот же паттерн, что был в исходном файле.
/// </summary>
public static class OptionsRegistration
{
    public static WebApplicationBuilder AddFamilyHubOptions(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));
        builder.Services.Configure<MinioOptions>(builder.Configuration.GetSection(MinioOptions.SectionName));
        builder.Services.Configure<NotificationOptions>(builder.Configuration.GetSection(NotificationOptions.SectionName));
        builder.Services.Configure<LmStudioOptions>(builder.Configuration.GetSection(LmStudioOptions.SectionName));
        builder.Services.Configure<EnrichmentOptions>(builder.Configuration.GetSection(EnrichmentOptions.SectionName));
        builder.Services.Configure<ExtractionOptions>(builder.Configuration.GetSection(ExtractionOptions.SectionName));
        builder.Services.Configure<ExtractionLimitsOptions>(builder.Configuration.GetSection(ExtractionLimitsOptions.SectionName));
        builder.Services.Configure<EncryptionOptions>(builder.Configuration.GetSection(EncryptionOptions.SectionName));
        builder.Services.Configure<AttachmentDownloadOptions>(builder.Configuration.GetSection(AttachmentDownloadOptions.SectionName));
        builder.Services.Configure<AttachmentUploadOptions>(builder.Configuration.GetSection(AttachmentUploadOptions.SectionName));
        builder.Services.Configure<PreviewOptions>(builder.Configuration.GetSection(PreviewOptions.SectionName));
        builder.Services.Configure<ConsentOptions>(builder.Configuration.GetSection(ConsentOptions.SectionName));
        builder.Services.Configure<WebPushOptions>(builder.Configuration.GetSection(WebPushOptions.SectionName));
        builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
        builder.Services.Configure<InternalOptions>(builder.Configuration.GetSection(InternalOptions.SectionName));
        // AuthRateLimitOptions читается ниже напрямую через GetSection().Get<>() (нужно синхронно, до
        // AddRateLimiter) — Configure<> здесь дополнительно, чтобы IOptions<AuthRateLimitOptions> был
        // резолвим через DI где угодно ещё (раньше не был зарегистрирован вовсе).
        builder.Services.Configure<AuthRateLimitOptions>(builder.Configuration.GetSection(AuthRateLimitOptions.SectionName));

        return builder;
    }

    /// <summary>DevTools (Hangfire/Swagger/DevAuth/`/dev/*`) + админ-панель (ADR-0009) — обе секции
    /// нужны синхронно ДО builder.Build() (используются в Authentication/Middleware-регистрации
    /// ниже), поэтому читаются здесь же вместе с fail-fast guard'ами, а не лениво через DI.</summary>
    public static (DevToolsOptions DevTools, AdminOptions Admin) AddDevToolsAndAdminGuards(this WebApplicationBuilder builder)
    {
        // --- DevTools (Hangfire/Swagger/DevAuth/`/dev/*`): раньше все четыре жёстко гейтились на
        // --- IsDevelopment(). Дев-контур на VPS работает под ASPNETCORE_ENVIRONMENT=Production (иначе
        // --- включается DeveloperExceptionPage, отдающий стектрейс наружу) — поэтому вынесены на флаги,
        // --- читаемые сразу (не только через DI Configure<>), т.к. используются ниже, до builder.Build().
        builder.Services.Configure<DevToolsOptions>(builder.Configuration.GetSection(DevToolsOptions.SectionName));
        var devToolsOptions = builder.Configuration.GetSection(DevToolsOptions.SectionName).Get<DevToolsOptions>()
            ?? new DevToolsOptions();
        if (devToolsOptions.AdminUiEnabled
            && (string.IsNullOrWhiteSpace(devToolsOptions.AdminUser) || string.IsNullOrWhiteSpace(devToolsOptions.AdminPassword)))
            throw new InvalidOperationException(
                "DevTools:AdminUiEnabled=true, но DevTools:AdminUser/AdminPassword (env DevTools__AdminUser/" +
                "DevTools__AdminPassword) не заданы — Hangfire-дашборд и Swagger были бы доступны без пароля.");

        // --- Админ-панель (ADR-0009): статистика + ротация ключей на отдельном домене за периметром ---
        // --- WireGuard/Caddy (admin.{PUBLIC_DOMAIN}:4059). Отдельная секция от DevTools:AdminUser/  ---
        // --- Password — см. AdminOptions.                                                            ---
        builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.SectionName));
        var adminOptions = builder.Configuration.GetSection(AdminOptions.SectionName).Get<AdminOptions>()
            ?? new AdminOptions();
        if (adminOptions.Enabled
            && (string.IsNullOrWhiteSpace(adminOptions.User) || string.IsNullOrWhiteSpace(adminOptions.Password)))
            throw new InvalidOperationException(
                "Admin:Enabled=true, но Admin:User/Password (env Admin__User/Admin__Password) не заданы — " +
                "форма входа админ-панели была бы недостижима (сравнение всегда отклонит любой ввод).");

        return (devToolsOptions, adminOptions);
    }
}
