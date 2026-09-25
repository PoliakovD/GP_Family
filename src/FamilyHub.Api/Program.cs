using FamilyHub.Api.Configuration;
using FamilyHub.Api.Features.Admin;
using FamilyHub.Api.Features.Ai;
using FamilyHub.Api.Features.Auth;
using FamilyHub.Api.Features.Account;
using FamilyHub.Api.Features.Bot;
using FamilyHub.Api.Features.Consents;
using FamilyHub.Api.Features.Dependents;
using FamilyHub.Api.Features.Dev;
using FamilyHub.Api.Features.Families;
using FamilyHub.Api.Features.Home;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Api.Features.Jobs;
using FamilyHub.Api.Features.Members;
using FamilyHub.Api.Features.Notifications;
using FamilyHub.Api.Features.Push;
using FamilyHub.Api.Startup;
using FamilyHub.Infrastructure.Consents;
using FamilyHub.Modules.Birthdays;
using FamilyHub.Modules.Medical;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

// Bootstrap-логгер ловит ошибки, которые случаются до того, как builder.Build()
// поднимет настоящий Serilog-логгер из конфигурации (например, сбой при чтении appsettings).
BootstrapLogging.ConfigureBootstrapLogger();

try
{
    Log.Information("Запуск FamilyHub.Api...");

    var builder = WebApplication.CreateBuilder(args);

    // --- Регистрация (композиция DI-графа) ---
    // Каждый вызов — секция, извлечённая из некогда 1229-строчного Program.cs в
    // src/FamilyHub.Api/Startup/ (по образцу единственного существовавшего в проекте аналога —
    // Infrastructure/Messaging/MassTransitRegistration.AddFamilyHubMessaging). Порядок вызовов
    // сохранён ровно тем же, каким он был в исходном файле — внутри DI-регистрации порядок между
    // РАЗНЫМИ типами сервисов не влияет на поведение, но сохранён для честности диффа и на случай
    // будущих находок, где порядок всё же важен (см. предупреждения ниже).
    builder.AddFamilyHubLogging();
    builder.AddFamilyHubOptions();
    var (devToolsOptions, adminOptions) = builder.AddDevToolsAndAdminGuards();
    builder.AddFamilyHubEncryption(devToolsOptions);
    var (_, postgresConnectionString) = builder.AddFamilyHubPersistence();
    builder.AddFamilyHubEventBus();
    builder.AddFamilyHubCurrentUser();
    builder.AddFamilyHubFileStorage();
    builder.AddFamilyHubCoreFeatures();
    builder.AddFamilyHubAuthentication(devToolsOptions, adminOptions);
    builder.AddFamilyHubAdminServices();
    builder.AddFamilyHubRateLimiting();
    builder.AddFamilyHubEmail();
    builder.AddFamilyHubAccountFeatures();
    builder.AddFamilyHubBackgroundJobs(postgresConnectionString);
    var (telegramBotConfigured, internalBotApiConfigured) = builder.AddFamilyHubNotificationChannels();
    builder.AddFamilyHubLmStudio();
    builder.AddFamilyHubAttachmentPipeline();
    builder.AddFamilyHubEnrichment();
    // Medical-модуль. ВАЖНО: AddFamilyHubMedicalAndBirthdays() регистрирует Null-реализацию
    // IMedicalDocumentExtractor по умолчанию (AddMedicalModule) и — если Extraction:Enabled — тут
    // же перекрывает её реальной; порядок ВНУТРИ этого вызова важен (ASP.NET Core DI резолвит
    // последнюю регистрацию для одиночного сервиса) и сохранён внутри самого метода.
    builder.AddFamilyHubMedicalAndBirthdays();
    builder.AddFamilyHubSwagger();
    builder.AddFamilyHubHealthChecks();
    builder.AddFamilyHubKestrelLimits();

    var app = builder.Build();

    // DevTools перерезолвлен из DI (не переиспользован pre-build devToolsOptions) — так было в
    // исходном коде: конфигурация не меняется между Build() и этой точкой, поведение то же.
    var devTools = app.Services.GetRequiredService<IOptions<DevToolsOptions>>().Value;

    // --- Pipeline (порядок критичен, сохранён ровно тем же, каким он был в исходном Program.cs) ---
    app.UseFamilyHubProxyHeaders();
    app.UseFamilyHubRequestLogging();
    app.MapFamilyHubHealthChecks();
    app.UseFamilyHubSwagger(devTools);

    // --- Раздача Telegram Mini App (React-сборка в wwwroot, этап 4 п.12) ---
    // До UseAuthorization: статика отдаётся без аутентификации, сам Mini App
    // аутентифицируется на уровне API-запросов через initData.
    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.UseFamilyHubSecurityHeaders();

    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();

    app.UseFamilyHubCsrfGate();
    app.UseNoStoreForApi();

    app.MapAuthEndpoints();
    app.MapTelegramBindingEndpoints();
    app.MapConsentEndpoints();
    app.MapAccountEndpoints();
    app.MapFamilyEndpoints();
    app.MapInviteEndpoints();
    app.MapMemberEndpoints();
    // Подопечные хранят ПДн (имя + дата рождения) — та же консент-гарантия, что у Medical/Birthdays
    // (см. BirthdayModule.MapBirthdayModule).
    app.MapGroup("").AddEndpointFilter<ConsentRequiredFilter>().MapFamilyDependentEndpoints();
    app.MapMedicalModule();
    app.MapBirthdayModule();
    // Агрегат Главной содержит медданные (статус анализов) — та же консент-гарантия (редизайн v2).
    app.MapGroup("").AddEndpointFilter<ConsentRequiredFilter>().MapHomeEndpoints();
    // Глобальный индикатор фоновых процессов содержит медданные (названия записей/показателей/
    // препаратов) — та же консент-гарантия, что у Главной/Medical выше.
    app.MapGroup("").AddEndpointFilter<ConsentRequiredFilter>().MapUserJobsEndpoints();
    app.MapAiStatusEndpoints();
    app.MapNotificationEndpoints();
    app.MapPushEndpoints();
    if (internalBotApiConfigured)
    {
        app.MapInternalBotEndpoints();
    }
    if (adminOptions.Enabled)
    {
        app.MapAdminSessionEndpoints();
        app.MapAdminEndpoints();
        app.MapAdminEnrichmentEndpoints();
        app.MapAdminWarmupEndpoints();
        app.MapAdminSearchCallsEndpoints();
        app.MapAdminPipelineEndpoints();
        app.MapAdminCatalogEndpoints();
        app.MapAdminLmStudioEndpoints();
    }

    // SPA-fallback для Mini App: любой нераспознанный путь отдаёт index.html
    // AllowAnonymous обязателен — иначе FallbackPolicy потребует аутентификацию и до R
    // дело не дойдёт даже для статических маршрутов приложения.
    app.MapFallbackToFile("index.html").AllowAnonymous();

    app.MapFamilyHubHangfireDashboard(devTools);
    app.MapDevEndpoints(devTools);

    app.UseFamilyHubRecurringJobs();

    // Применение миграций с retry для transient-ошибок при старте (race-condition нескольких реплик)
    await app.MigrateDatabaseWithRetryAsync();

    app.EnqueueStartupMaintenanceJobs();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    // HostAbortedException прилетает от `dotnet ef` (design-time сборка хоста) — это не сбой.
    Log.Fatal(ex, "FamilyHub.Api аварийно завершился при запуске");
}
finally
{
    Log.CloseAndFlush();
}

// Сгенерированный для top-level statements класс Program по умолчанию internal — для
// WebApplicationFactory<Program> в интеграционных тестах нужен public.
public partial class Program { }
