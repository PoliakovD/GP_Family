using System.Threading.RateLimiting;
using FamilyHub.Api.Configuration;
using FamilyHub.Api.Features.Auth;
using FamilyHub.Api.Features.Account;
using FamilyHub.Api.Features.Bot;
using FamilyHub.Api.Features.Consents;
using FamilyHub.Api.Features.Dependents;
using FamilyHub.Api.Features.Families;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Api.Features.Members;
using FamilyHub.Api.Health;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using FamilyHub.Api.Security;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Audit;
using FamilyHub.Infrastructure.Auth;
using FamilyHub.Infrastructure.Auth.Jwt;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Consents;
using FamilyHub.Api.Features.Notifications;
using FamilyHub.Api.Features.Push;
using FamilyHub.Infrastructure.CurrentUser;
using Amazon.SimpleEmailV2;
using FamilyHub.Infrastructure.Email;
using FamilyHub.Infrastructure.Email.Templates;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Contracts.Events;
using FamilyHub.Infrastructure.Messaging;
using FamilyHub.Infrastructure.Notifications;
using FamilyHub.Infrastructure.Notifications.Consumers;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Infrastructure.Security;
using FamilyHub.Infrastructure.Storage;
using FamilyHub.Infrastructure.Telegram;
using FamilyHub.Modules.Birthdays;
using FamilyHub.Modules.Medical;
using FamilyHub.Modules.Medical.Consumers;
using FamilyHub.Modules.Medical.Enrichment;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Minio;
using Serilog;
using Serilog.Events;
using Serilog.Exceptions;
using System.Net;

// Bootstrap-логгер ловит ошибки, которые случаются до того, как builder.Build()
// поднимет настоящий Serilog-логгер из конфигурации (например, сбой при чтении appsettings).
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Запуск FamilyHub.Api...");

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithExceptionDetails()
    .Enrich.WithEnvironmentName()
    .Enrich.WithMachineName());

// --- Конфигурация ---
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));
builder.Services.Configure<MinioOptions>(builder.Configuration.GetSection(MinioOptions.SectionName));
builder.Services.Configure<NotificationOptions>(builder.Configuration.GetSection(NotificationOptions.SectionName));
builder.Services.Configure<LmStudioOptions>(builder.Configuration.GetSection(LmStudioOptions.SectionName));
builder.Services.Configure<EnrichmentOptions>(builder.Configuration.GetSection(EnrichmentOptions.SectionName));
builder.Services.Configure<EncryptionOptions>(builder.Configuration.GetSection(EncryptionOptions.SectionName));
builder.Services.Configure<AttachmentDownloadOptions>(builder.Configuration.GetSection(AttachmentDownloadOptions.SectionName));
builder.Services.Configure<ConsentOptions>(builder.Configuration.GetSection(ConsentOptions.SectionName));
builder.Services.Configure<WebPushOptions>(builder.Configuration.GetSection(WebPushOptions.SectionName));
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<InternalOptions>(builder.Configuration.GetSection(InternalOptions.SectionName));

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

// --- At-rest шифрование (этап 2, 152-ФЗ): ключ вне БД, fail-fast при отсутствии ---
// Синглтоны обязательны: EF кэширует модель с конвертером, захватившим первый cipher.
var encryptionMasterKey = builder.Configuration["Encryption:MasterKey"];
if (string.IsNullOrWhiteSpace(encryptionMasterKey))
    throw new InvalidOperationException(
        "Encryption:MasterKey не задан (env Encryption__MasterKey) — at-rest шифрование обязательно.");
// appsettings.Development.json и docker-compose.yml больше НЕ содержат дефолт этого ключа —
// секреты везде тянутся из окружения, даже в Development (см. .env.example). Единственное
// оставшееся легитимное место с этим значением — DesignTimeDbContextFactory.DevMasterKey
// (design-time `dotnet ef`/тестовые фабрики, реальных данных не касается). Но строка всё
// равно навсегда осталась в истории git — этот guard блокирует её случайное копирование в
// реальное окружение. Условие теперь завязано на DevTools:DevAuthEnabled, а не на
// ASPNETCORE_ENVIRONMENT — контур на VPS дев по защите, но Production по среде (см. выше).
if (!devToolsOptions.DevAuthEnabled && encryptionMasterKey == DesignTimeDbContextFactory.DevMasterKey)
    throw new InvalidOperationException(
        "Encryption:MasterKey равен design-time/тестовому dev-ключу из истории репозитория — " +
        "при выключенном DevTools:DevAuthEnabled это недопустимо. Сгенерировать реальный ключ: " +
        "`openssl rand -base64 32`.");
builder.Services.AddSingleton<IFieldCipher, AesGcmFieldCipher>();
builder.Services.AddSingleton<IFileCipher, AesGcmFileCipher>();
builder.Services.AddSingleton<DownloadTokenService>();

// Fail-fast для ключа подписи ссылок на скачивание вложений — без него DownloadTokenService.Sign
// бросал бы лениво, только при первой попытке выдать ссылку (см. находку 09.2 аудита безопасности).
if (string.IsNullOrWhiteSpace(builder.Configuration["Attachments:DownloadSigningKey"]))
    throw new InvalidOperationException(
        "Attachments:DownloadSigningKey не задан (env Attachments__DownloadSigningKey) — " +
        "выдача ссылок на вложения невозможна.");

// Стеммер/триграммы — чистые функции без состояния (этап 3, ADR-0003): singleton безопасен.
// Общий для Modules.Medical (медкарты) и Modules.Birthdays (дни рождения) — оба зависят только
// от Domain/Infrastructure и не ссылаются друг на друга, поэтому регистрация — здесь, не в модуле.
builder.Services.AddSingleton<IRussianTextSearcher, RussianTextSearcher>();

// --- Persistence ---
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Postgres.")));

// --- Data Protection (отладка 2026-08-20): без этого ключи живут в эфемерной ФС контейнера ---
// (~/.aspnet/DataProtection-Keys) — каждый перезапуск/редеплой api сбрасывает их, инвалидируя
// CSRF-токены (IAntiforgery, единственный потребитель Data Protection в этом приложении — JWT
// подписывается отдельным Jwt:SigningKey, не затронут) у всех активных сессий.
// PersistKeysToDbContext — та же Postgres, что и остальное состояние, автоматически попадает
// под уже настроенный ночной pg_dump (см. deploy/backup).
builder.Services.AddDataProtection()
    .SetApplicationName("FamilyHub")
    .PersistKeysToDbContext<AppDbContext>();

// --- Событийная шина: MassTransit + EF Core Outbox + Kafka Rider (ADR-0006/ADR-0007) ---
// Messaging:Kafka:Enabled=true (docker-compose/прод, дефолт для полного стека) — бизнес-потребители
// подписаны на Kafka Rider (явный список ниже, composition root — единственное место, которому
// позволено знать конкретные типы потребителей ИЗ ВСЕХ модулей сразу); false (dev-lite/юнит-тесты,
// без Docker) — потребители сканом сборок на InMemory, как раньше. В обоих случаях сбой одного
// потребителя не касается соседа — топология шины (свой receive endpoint/consumer group), не наш
// код, как раньше у IsolatingLoggingPublisher.
var kafkaConsumers = new KafkaConsumerRegistration[]
{
    new(typeof(MedicalRecordSharedEvent), typeof(MedicalRecordSharedNotificationConsumer), "notifications-medical-record-shared"),
    new(typeof(UserLeftFamilyEvent), typeof(UserLeftFamilyNotificationConsumer), "notifications-user-left-family"),
    new(typeof(UserLeftFamilyEvent), typeof(UserLeftFamilyMedicalCleanupConsumer), "medical-user-left-family"),
    new(typeof(MemberApprovedEvent), typeof(MemberApprovedNotificationConsumer), "notifications-member-approved"),
    new(typeof(MedicationExpiringEvent), typeof(MedicationExpiringNotificationConsumer), "notifications-medication-expiring"),
    new(typeof(BirthdayApproachingEvent), typeof(BirthdayApproachingNotificationConsumer), "notifications-birthday-approaching"),
    new(typeof(MedicationEnrichedEvent), typeof(MedicationEnrichedNotificationConsumer), "notifications-medication-enriched"),
};
builder.Services.AddFamilyHubMessaging(builder.Configuration, kafkaConsumers,
    typeof(DomainEventPublisher).Assembly,
    typeof(MedicalModule).Assembly,
    typeof(BirthdayModule).Assembly);

// --- Текущий пользователь / провижининг ---
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
builder.Services.AddScoped<IUserProvisioningService, UserProvisioningService>();

// --- Telegram auth ---
builder.Services.AddScoped<ITelegramInitDataValidator, TelegramInitDataValidator>();

// --- Хранилище файлов: MinIO — единственная реализация IFileStorage, в т.ч. в Development ---
// Раньше был переключатель FileStorage:Provider = Local|Minio: запуск из IDE тихо писал
// медицинские сканы на диск мимо объектного хранилища, и этот путь никогда не проверялся.
// Fail-fast на пустые креды — без него ошибка всплыла бы только при первой загрузке файла.
if (string.IsNullOrWhiteSpace(builder.Configuration["Minio:Endpoint"])
    || string.IsNullOrWhiteSpace(builder.Configuration["Minio:AccessKey"])
    || string.IsNullOrWhiteSpace(builder.Configuration["Minio:SecretKey"]))
    throw new InvalidOperationException(
        "Minio:Endpoint/AccessKey/SecretKey не заданы (env Minio__Endpoint/Minio__AccessKey/" +
        "Minio__SecretKey) — хранилище вложений обязательно, в т.ч. в Development (см. docker-compose.yml).");

builder.Services.AddSingleton<IMinioClient>(sp =>
{
    var minioOptions = sp.GetRequiredService<IOptions<MinioOptions>>().Value;
    return (IMinioClient)new MinioClient()
        .WithEndpoint(minioOptions.Endpoint)
        .WithCredentials(minioOptions.AccessKey, minioOptions.SecretKey)
        .WithSSL(minioOptions.UseSsl)
        .Build();
});
builder.Services.AddSingleton<IFileStorage, MinioFileStorage>();

// --- Core-фичи: семьи, приглашения, участники, подопечные ---
builder.Services.AddScoped<FamilyService>();
builder.Services.AddScoped<InviteService>();
builder.Services.AddScoped<MembershipService>();
builder.Services.AddScoped<FamilyDependentService>();

// --- Авторизация по ролям в семье ---
builder.Services.AddScoped<IFamilyAccessService, FamilyAccessService>();
builder.Services.AddScoped<IAuthorizationHandler, FamilyRoleHandler>();
builder.Services.AddAuthorization(options =>
{
    // Защита по умолчанию: любой эндпоинт без явной политики всё равно требует аутентификации.
    options.FallbackPolicy = options.DefaultPolicy;
});

// --- JWT PWA-сессия: access-токен в httpOnly cookie + refresh-токен в БД (ротация,   ---
// --- reuse-detection, revoke-all). Fail-fast: ключ подписи обязателен во всех средах. ---
var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwtOptions.SigningKey))
    throw new InvalidOperationException("Jwt:SigningKey не задан (env Jwt__SigningKey) — JWT-сессии PWA невозможны.");

byte[] jwtSigningKeyBytes;
try
{
    jwtSigningKeyBytes = Convert.FromBase64String(jwtOptions.SigningKey);
}
catch (FormatException ex)
{
    // Без этой проверки ошибка проявлялась бы не при старте, а лениво — на первый же
    // входящий запрос, внутри IOptionsFactory для JwtBearer (см. AddJwtBearer ниже), и
    // валила бы 500 АБСОЛЮТНО любой запрос (включая AllowAnonymous — аутентификация
    // пытается резолвить дефолтную схему до authorization независимо от эндпоинта). Самый
    // частый источник — незаменённый плейсхолдер `Jwt__SigningKey=CHANGE_ME` из .env.example
    // (не валиден как Base64: недопустимый символ `_` и некорректная длина).
    throw new InvalidOperationException(
        "Jwt:SigningKey (env Jwt__SigningKey) задан, но не является корректной Base64-строкой — " +
        "похоже на незаменённый плейсхолдер из .env.example. Сгенерировать реальный ключ: " +
        "`openssl rand -base64 32` (или PowerShell: " +
        "[Convert]::ToBase64String((1..32|%{Get-Random -Max 256}))).", ex);
}
builder.Services.AddScoped<ITokenService, TokenService>();

// --- CSRF: double-submit антифорджери-токен поверх SameSite=Lax для PWA-cookie сессии (аудит
// --- module-review-2026-08-02/01-auth-identity.md, находка 4). Только PWA — Telegram Mini App
// --- аутентифицируется явным initData в заголовке, ambient-cookie CSRF к нему неприменим.
// --- Cookie.Name — приватная (httpOnly) половина токена; публичную, которую читает Angular
// --- (withXsrfConfiguration), выставляет PwaSessionCookieWriter.IssueCsrfCookie отдельно.
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "familyhub.csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    // Path обязателен явно: без него браузер/CookieContainer скоупит cookie по RFC 6265
    // default-path (директория ПЕРВОГО запроса, который её выставил — например
    // "/api/auth/register", если сессия открыта регистрацией) и она не долетает до
    // остальных /api-путей на следующих мутирующих запросах.
    options.Cookie.Path = "/";
    options.HeaderName = "X-XSRF-TOKEN";
});

// --- Аутентификация: два окружения (этап 2 п.2.4) — Telegram Mini App и PWA-JWT,      ---
// --- плюс Dev-заглушка (DevTools:DevAuthEnabled, НЕ привязана к ASPNETCORE_ENVIRONMENT —---
// --- см. DevToolsOptions). Селектор "Smart" во всех средах:                           ---
// --- tma-заголовок → Telegram; X-Dev-TelegramId (dev) → Dev; иначе → JWT.             ---
var authBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = AuthSchemes.Smart;
    options.DefaultAuthenticateScheme = AuthSchemes.Smart;
    options.DefaultChallengeScheme = AuthSchemes.Smart;
});

authBuilder.AddScheme<AuthenticationSchemeOptions, TelegramMiniAppAuthenticationHandler>(AuthSchemes.TelegramMiniApp, null);

authBuilder.AddJwtBearer(AuthSchemes.PwaCookie, jwtBearerOptions =>
{
    jwtBearerOptions.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = jwtOptions.Issuer,
        ValidateAudience = true,
        ValidAudience = jwtOptions.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(jwtSigningKeyBytes),
        ValidateLifetime = true,
        ClockSkew = jwtOptions.ClockSkew,
    };
    jwtBearerOptions.Events = new JwtBearerEvents
    {
        // Access-токен ездит в httpOnly cookie, а не в заголовке Authorization —
        // PWA-запросы идут через withCredentials, не bearer-заголовок.
        OnMessageReceived = ctx =>
        {
            if (ctx.Request.Cookies.TryGetValue(PwaCookieNames.AccessToken, out var accessToken))
                ctx.Token = accessToken;
            return Task.CompletedTask;
        },
        // SPA-API: вместо WWW-Authenticate-челленджа отдаём голый 401, как и раньше у cookie-схемы.
        OnChallenge = ctx =>
        {
            ctx.HandleResponse();
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        },
    };
});

if (devToolsOptions.DevAuthEnabled)
{
    authBuilder.AddScheme<AuthenticationSchemeOptions, DevAuthenticationHandler>(AuthSchemes.Dev, null);
}

authBuilder.AddPolicyScheme(AuthSchemes.Smart, AuthSchemes.Smart, policyOptions =>
{
    policyOptions.ForwardDefaultSelector = httpContext =>
    {
        var request = httpContext.Request;
        var hasInitData = request.Headers.Authorization.ToString().StartsWith("tma ", StringComparison.Ordinal)
            || request.Headers.ContainsKey("X-Telegram-Init-Data");
        if (hasInitData) return AuthSchemes.TelegramMiniApp;
        if (devToolsOptions.DevAuthEnabled && request.Headers.ContainsKey("X-Dev-TelegramId")) return AuthSchemes.Dev;
        return AuthSchemes.PwaCookie;
    };
});

// --- Rate limiting PWA-auth (брутфорс-защита, этап 2 п.2.4). Лимиты конфигурируемы —
// --- интеграционные тесты поднимают их, чтобы не ловить 429 на обычных сценариях.
var authRateLimits = builder.Configuration.GetSection(AuthRateLimitOptions.SectionName).Get<AuthRateLimitOptions>() ?? new AuthRateLimitOptions();
builder.Services.AddRateLimiter(limiterOptions =>
{
    limiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Партиция — по IP клиента: лимит общий для всех auth-эндпоинтов с этой политикой.
    limiterOptions.AddPolicy("auth", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authRateLimits.AuthPermitLimit,
            Window = TimeSpan.FromSeconds(authRateLimits.AuthWindowSeconds),
            QueueLimit = 0,
        }));

    // Жёстче для выдачи email-кодов: каждая выдача — реальное письмо.
    limiterOptions.AddPolicy("auth-code", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authRateLimits.CodePermitLimit,
            Window = TimeSpan.FromSeconds(authRateLimits.CodeWindowSeconds),
            QueueLimit = 0,
        }));

    // Погашение инвайт-кода — вне группы /api/auth, поэтому политика "auth" сюда не
    // распространяется; отдельная политика ради единообразия модели защиты (см. аудит,
    // находка 02.2), а не из-за реальной практичности перебора 128-битного кода.
    limiterOptions.AddPolicy("invite-redeem", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authRateLimits.RedeemPermitLimit,
            Window = TimeSpan.FromSeconds(authRateLimits.RedeemWindowSeconds),
            QueueLimit = 0,
        }));
});

// --- PWA-auth сервисы + email-отправка (задача 2.5, расширено 2026-08-19) ---
// Email:PostboxApi (HTTPS, порт 443) и/или Email:Providers (SMTP, failover, задача 2.5) —
// оба канала опциональны и независимы; если задан хотя бы один — CompositeEmailSender пробует
// их по порядку (Postbox API первым: SMTP-порты 587/465 оказались заблокированы у части
// провайдеров связи, см. отладку 2026-08-19). Ни один не задан — LoggingEmailSender (dev).
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
builder.Services.AddScoped<EmailOtpService>();
builder.Services.AddScoped<PwaAuthService>();
builder.Services.AddScoped<TelegramBindingService>();
// HTML-вёрстка писем (see docs plan): рендерер не зависит от того, какой IEmailSender выбран
// ниже — регистрируем его безусловно, чтобы dev/тесты тоже видели настоящий рендер (опечатка
// в плейсхолдере шаблона должна ронять сборку/тесты, а не только молчать в проде).
builder.Services.AddSingleton<EmailTemplateRenderer>();
var emailProvidersConfigured = builder.Configuration.GetSection($"{EmailOptions.SectionName}:Providers").GetChildren().Any();
var postboxApiSection = builder.Configuration.GetSection($"{EmailOptions.SectionName}:PostboxApi");
var postboxApiConfigured = postboxApiSection.GetChildren().Any();
if (postboxApiConfigured)
{
    var postboxApiOptions = postboxApiSection.Get<YandexPostboxApiOptions>() ?? new YandexPostboxApiOptions();
    if (string.IsNullOrWhiteSpace(postboxApiOptions.AccessKeyId) || string.IsNullOrWhiteSpace(postboxApiOptions.SecretAccessKey)
        || string.IsNullOrWhiteSpace(postboxApiOptions.From))
    {
        throw new InvalidOperationException(
            "Email:PostboxApi задан, но AccessKeyId/SecretAccessKey/From (env Email__PostboxApi__*) пусты — " +
            "это отдельный статический access-key Yandex Cloud, не логин/пароль SMTP.");
    }

    builder.Services.AddSingleton<IAmazonSimpleEmailServiceV2>(_ => new AmazonSimpleEmailServiceV2Client(
        postboxApiOptions.AccessKeyId, postboxApiOptions.SecretAccessKey,
        new AmazonSimpleEmailServiceV2Config { ServiceURL = postboxApiOptions.ServiceUrl, AuthenticationRegion = postboxApiOptions.Region }));
    builder.Services.AddSingleton<YandexPostboxApiEmailSender>();
}
if (emailProvidersConfigured)
{
    builder.Services.AddSingleton<ISmtpTransport, MailKitSmtpTransport>();
    builder.Services.AddSingleton<MailKitSmtpEmailSender>();
}
if (postboxApiConfigured || emailProvidersConfigured)
{
    // Хотя бы один канал задан ⇒ письма уходят наружу ⇒ ссылка в кнопке «Открыть FamilyHub»
    // обязана быть настоящей. Проверка схемы заодно закрывает подстановку javascript:-URL в
    // href шаблона.
    var publicSiteUrl = builder.Configuration[$"{EmailOptions.SectionName}:PublicSiteUrl"];
    if (!Uri.TryCreate(publicSiteUrl, UriKind.Absolute, out var siteUri)
        || (siteUri.Scheme != Uri.UriSchemeHttps && siteUri.Scheme != Uri.UriSchemeHttp))
    {
        throw new InvalidOperationException(
            "Email:PublicSiteUrl должен быть абсолютным http(s)-URL — он подставляется в кнопку «Открыть FamilyHub» в письмах.");
    }

    // Порядок важен: Postbox API — первым (подтверждённо работает через 443), SMTP — резервным.
    builder.Services.AddSingleton<IEmailSender>(sp =>
    {
        var channels = new List<IEmailSender>();
        if (postboxApiConfigured) channels.Add(sp.GetRequiredService<YandexPostboxApiEmailSender>());
        if (emailProvidersConfigured) channels.Add(sp.GetRequiredService<MailKitSmtpEmailSender>());
        return new CompositeEmailSender(channels, sp.GetRequiredService<ILogger<CompositeEmailSender>>());
    });
}
else
{
    builder.Services.AddScoped<IEmailSender, LoggingEmailSender>();
}

// --- Согласия ПДн (задача 2.3): версия + принятие + кэш для ConsentRequiredFilter ---
builder.Services.AddMemoryCache();
builder.Services.AddScoped<ConsentService>();

// --- Права субъекта ПДн (задача 2.3): удаление аккаунта + экспорт ---
builder.Services.AddScoped<AccountService>();

// --- Привязка Telegram к веб-аккаунту с подтверждением от бота + слияние аккаунтов ---
builder.Services.AddScoped<AccountMergeService>();
builder.Services.AddScoped<TelegramLinkService>();

// --- Аудит доступа к медданным (задача 2.7): синхронная запись + ретеншн-джоба ---
builder.Services.AddScoped<IMedicalAuditWriter, MedicalAuditWriter>();
builder.Services.AddScoped<AuditRetentionJob>();

// --- Оповещения: Hangfire recurring job по срокам годности лекарств и дням рождения (этап 3 п.10) ---
var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Postgres.");
builder.Services.AddHangfire(cfg => cfg.UsePostgreSqlStorage(opt => opt.UseNpgsqlConnection(postgresConnectionString)));
builder.Services.AddHangfireServer(o => o.Queues = ["default"]);
// Второй сервер, выделенная очередь "enrichment" (этап 4) с ОДНИМ воркером: обогащение
// справочника не должно отъедать пропускную способность у ReminderScanJob/AuditRetentionJob,
// а один воркер естественно укладывается в лимит внешнего поиска (Brave free-tier — 1 req/s),
// без отдельного rate-limiter в коде (см. MedicationEnrichmentProcessor).
builder.Services.AddHangfireServer(o =>
{
    o.Queues = ["enrichment"];
    o.WorkerCount = 1;
    o.ServerName = "enrichment-server";
});
builder.Services.AddScoped<ReminderScanJob>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<NotificationSendingService>();
builder.Services.AddScoped<PushSubscriptionService>();

// --- Telegram: доставка оповещений через шину (этап 4 п.12; бот сам живёт в отдельном ---
// --- процессе FamilyHub.TelegramBot, см. ADR-0008) ---
// BotToken по-прежнему обязателен здесь: TelegramInitDataValidator выводит из него HMAC-ключ
// для проверки initData Mini App — это забота Api, а не бота. Сам бот (вебхук, SendMessage)
// не живёт в этом процессе больше; TelegramOutboundPublisher публикует готовое сообщение в
// Kafka (topic telegram-outbound), которое потребляет FamilyHub.TelegramBot.
var telegramBotToken = builder.Configuration["Telegram:BotToken"];
var telegramBotConfigured = !string.IsNullOrWhiteSpace(telegramBotToken);
if (telegramBotConfigured)
{
    builder.Services.AddScoped<INotificationSender, TelegramOutboundPublisher>();
}

// --- Внутренний API для FamilyHub.TelegramBot (/internal/bot/*, см. InternalBotEndpoints) ---
// Отдельный флаг от telegramBotConfigured: этот секрет защищает контур обмена с ботом-процессом,
// а не с Telegram напрямую, и может быть сконфигурирован независимо (напр. в проде — всегда,
// в локальной разработке без контейнера бота — не обязателен).
var internalBotApiToken = builder.Configuration["Internal:BotApiToken"];
var internalBotApiConfigured = !string.IsNullOrWhiteSpace(internalBotApiToken);
if (internalBotApiConfigured && internalBotApiToken!.Length < 32)
    throw new InvalidOperationException(
        "Internal:BotApiToken (env Internal__BotApiToken) короче 32 символов — секрет обмена с " +
        "FamilyHub.TelegramBot слишком слабый. Сгенерировать: `openssl rand -hex 32`.");

// --- Web Push: реальная доставка PWA-пользователям (редизайн навигации, ADR-0004) — покрывает
// пользователей без Telegram, которых TelegramNotificationSender не видит вовсе. Независимо от
// Telegram-канала: оба могут быть настроены одновременно (NotificationSendingService.TrySendAsync
// фан-аутит на ВСЕ зарегистрированные INotificationSender, см. IEnumerable<INotificationSender>).
var webPushOptions = builder.Configuration.GetSection(WebPushOptions.SectionName).Get<WebPushOptions>();
var webPushConfigured = webPushOptions?.IsConfigured == true;
if (webPushConfigured)
{
    builder.Services.AddSingleton<WebPush.IWebPushClient>(sp =>
    {
        var options = sp.GetRequiredService<IOptions<WebPushOptions>>().Value;
        var client = new WebPush.WebPushClient();
        client.SetVapidDetails(options.Subject, options.VapidPublicKey, options.VapidPrivateKey);
        return client;
    });
    builder.Services.AddScoped<INotificationSender, WebPushNotificationSender>();
}

// Ни один реальный канал не настроен (типично — локальный dev) — доставка остаётся в логах.
if (!telegramBotConfigured && !webPushConfigured)
{
    builder.Services.AddScoped<INotificationSender, LoggingNotificationSender>();
}

// --- LM Studio: локальная LLM (текст + vision) — оцифровка медикаментов по фото (не хранит
// --- фото) и суммаризация веб-сниппетов для справочника (этап 4) ---
builder.Services.AddHttpClient<ILmStudioJsonClient, LmStudioJsonClient>((sp, client) =>
{
    var lmStudioOptions = sp.GetRequiredService<IOptions<LmStudioOptions>>().Value;
    client.BaseAddress = new Uri(lmStudioOptions.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(lmStudioOptions.TimeoutSeconds);
});

// --- Enrichment: внешний веб-поиск для обогащения справочника препаратов (этап 4, ADR-0005) ---
// Переключатель Enrichment:Provider = Null|Brave|Yandex (тот же паттерн конфиг-переключателя,
// что раньше был у FileStorage:Provider, пока хранилище не свели к единственной реализации).
// Без явного конфига — Null: наружу не уходит ни одного запроса (см. NullMedicationSearchProvider).
var enrichmentOptions = builder.Configuration.GetSection(EnrichmentOptions.SectionName).Get<EnrichmentOptions>()
    ?? new EnrichmentOptions();
if (enrichmentOptions.Provider != MedicationSearchProviderKind.Null && string.IsNullOrWhiteSpace(enrichmentOptions.ApiKey))
{
    throw new InvalidOperationException(
        $"Enrichment:Provider={enrichmentOptions.Provider} задан, но Enrichment:ApiKey (env Enrichment__ApiKey) пуст.");
}
if (enrichmentOptions.Provider == MedicationSearchProviderKind.Yandex && string.IsNullOrWhiteSpace(enrichmentOptions.FolderId))
{
    throw new InvalidOperationException(
        "Enrichment:Provider=Yandex задан, но Enrichment:FolderId (env Enrichment__FolderId) пуст — " +
        "обязателен для Yandex Web Search API v2/gen/search.");
}
switch (enrichmentOptions.Provider)
{
    case MedicationSearchProviderKind.Brave:
        builder.Services.AddHttpClient<IMedicationSearchProvider, BraveSearchProvider>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<EnrichmentOptions>>().Value;
            client.BaseAddress = new Uri("https://api.search.brave.com/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });
        break;
    case MedicationSearchProviderKind.Yandex:
        builder.Services.AddHttpClient<IMedicationSearchProvider, YandexSearchProvider>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<EnrichmentOptions>>().Value;
            client.BaseAddress = new Uri("https://searchapi.api.cloud.yandex.net/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });
        break;
    default:
        builder.Services.AddScoped<IMedicationSearchProvider, NullMedicationSearchProvider>();
        break;
}

// --- Medical-модуль ---
builder.Services.AddMedicalModule();

// --- Birthdays-модуль (этап 4 п.11) ---
builder.Services.AddBirthdayModule();

// --- Swagger (ручное тестирование) ---
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// --- Health checks: нужны и для депло-пайплайна (ждать /health/ready перед переключением
// --- трафика), и для docker-compose depends_on: service_healthy. Раньше в проекте не было ни
// --- одного. "llm" — отдельный тег: LM Studio на ноутбуке пользователя за WireGuard, его
// --- недоступность (сон/выключен) ожидаема и не должна валить общую готовность (тег "ready").
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"])
    .AddCheck<MinioHealthCheck>("minio", tags: ["ready"])
    .AddCheck<KafkaHealthCheck>("kafka", tags: ["ready"])
    .AddCheck<LmStudioHealthCheck>("llm", tags: ["llm"]);

// Явный запас над AttachmentService.MaxSizeBytes (30 МиБ): без этого implicit-дефолт Kestrel
// (~28.6 МиБ, 30_000_000 байт) обрубал бы запрос СВОЕЙ, менее информативной ошибкой раньше,
// чем срабатывала бы наша проверка с понятным телом ответа ({code, maxSizeBytes}) — см. аудит
// module-review-2026-08-02/03-medical-records-attachments.md, находка 2.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 40 * 1024 * 1024);

var app = builder.Build();

var devTools = app.Services.GetRequiredService<IOptions<DevToolsOptions>>().Value;

// --- Заголовки от Caddy (реверс-прокси, деплой-план): без этого Request.Scheme всегда "http" —
// --- secure-куки (JWT access-cookie, CSRF) выставлялись бы без Secure, а RemoteIpAddress у ВСЕХ
// --- запросов стал бы адресом Caddy (ломает партиционирование rate limiter по IP выше и
// --- RemoteIp в логах Seq). KnownNetworks — весь диапазон docker-мостов по умолчанию (172.16/12),
// --- не "доверяю всем" (X-Forwarded-* игнорируются от источников вне этой сети).
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    KnownIPNetworks = { new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12) },
});

// --- Структурированное логирование HTTP-запросов (метод, путь, статус, время, пользователь) в Seq ---
// Уровень поднимается на 4xx/5xx и падает на Debug для успешных запросов к статике/Hangfire/health —
// иначе Seq захлёстывает шумом от каждого ассета SPA и от healthcheck-поллинга раз в несколько секунд.
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

// --- Health checks: /health/live — процесс жив (без проверок, для liveness-проб); /health/ready —
// --- зависимости на месте (Postgres/MinIO/Kafka, тег "ready"); /health/llm — отдельно, т.к. LM
// --- Studio на ноутбуке за WireGuard и его недоступность не должна валить readiness всего контура.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
}).AllowAnonymous();
app.MapHealthChecks("/health/llm", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("llm"),
}).AllowAnonymous();

// --- Swagger (ручное тестирование) — раньше только Development, теперь DevTools:AdminUiEnabled
// --- (см. DevToolsOptions): на VPS доступен за тем же BasicAuth, что и Hangfire-дашборд ниже,
// --- поверх периметра (Caddy пускает /swagger только на WireGuard-адресе).
if (devTools.AdminUiEnabled)
{
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
}

// --- Раздача Telegram Mini App (React-сборка в wwwroot, этап 4 п.12) ---
// До UseAuthorization: статика отдаётся без аутентификации, сам Mini App
// аутентифицируется на уровне API-запросов через initData.
app.UseDefaultFiles();
app.UseStaticFiles();

// --- Заголовки безопасности (аудит module-review-2026-08-02/08-web-frontend-angular.md,
// --- находка 2 / 09-config-deployment-devops.md, находка 3): раньше не выставлялись нигде —
// --- ни на уровне бэкенда (сам раздаёт SPA-сборку через UseStaticFiles/MapFallbackToFile выше,
// --- отдельного реверс-прокси в репозитории нет), ни в index.html. CSP — базовый, под реальные
// --- нужды текущего фронтенда (без внешних CDN — шрифты/иконки забандлены, единственный внешний
// --- script-src — телеграмовский SDK, подгружаемый условно, см. index.html): style-src требует
// --- 'unsafe-inline' — Angular без CSP-nonce вставляет component-стили инлайново, это стандартное
// --- и ожидаемое ограничение, не наша недоработка.
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
        "img-src 'self' data:; " +
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

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// --- CSRF-гейт (аудит module-review-2026-08-02/01-auth-identity.md, находка 4): мутирующий
// --- /api-запрос, несущий публичную cookie CsrfCookieNames.PublicToken (выставляется ТОЛЬКО
// --- вместе с PWA-сессией, см. PwaSessionCookieWriter.IssueCsrfCookie), обязан нести валидный
// --- заголовок X-XSRF-TOKEN. Telegram/Dev-запросы эту cookie никогда не получают — пропускаются
// --- естественно, без отдельной проверки auth-схемы. IsRequestValidAsync при наличии заголовка
// --- читает токен ИЗ заголовка, не трогая тело запроса — безопасно и для multipart-загрузок.
app.Use(async (context, next) =>
{
    var method = context.Request.Method;
    var isMutating = HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
        || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
    if (isMutating && context.Request.Path.StartsWithSegments("/api")
        && context.Request.Cookies.ContainsKey(CsrfCookieNames.PublicToken))
    {
        var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
        if (!await antiforgery.IsRequestValidAsync(context))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { code = "csrf_token_invalid" });
            return;
        }
    }
    await next();
});

// Явный запрет кэширования для всех /api-ответов. Обнаружен случай (Telegram Mini App
// WebView), когда GET /api/auth/me иногда получал закэшированный где-то на клиенте
// index.html вместо актуального JSON, хотя прямые HTTP-проверки того же бэкенда/прокси
// всегда отвечали корректно — сам ответ API не запрещал явно своё кэширование. Response
// Cache-Control — авторитетный сигнал для любого кэша (клиентского, прокси), надёжнее
// одних только запросных заголовков.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/internal"))
    {
        context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.Response.Headers.Pragma = "no-cache";
    }
    await next();
});

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
app.MapNotificationEndpoints();
app.MapPushEndpoints();
if (internalBotApiConfigured)
{
    app.MapInternalBotEndpoints();
}

// SPA-fallback для Mini App: любой нераспознанный путь отдаёт index.html (React-роутинг).
// AllowAnonymous обязателен — иначе FallbackPolicy потребует аутентификацию и до React
// дело не дойдёт даже для статических маршрутов приложения.
app.MapFallbackToFile("index.html").AllowAnonymous();

// --- Дашборд Hangfire — DevTools:AdminUiEnabled (см. DevToolsOptions). Раньше был только
// --- Development с пустым Authorization (см. историю), что означало анонимный доступ в тот же
// --- момент, когда контур становится Production по среде (дев-контур на VPS, см. деплой-план) —
// --- поэтому здесь собственный BasicAuth-фильтр, а не голое AllowAnonymous.
if (devTools.AdminUiEnabled)
{
    // AllowAnonymous обязателен: FallbackPolicy выше требует аутентификации для всех
    // эндпоинтов без явного исключения, а у браузера при заходе на /hangfire нет ни
    // Telegram initData, ни dev-заголовка X-Dev-TelegramId — реальная проверка личности здесь
    // теперь HangfireBasicAuthFilter, не ASP.NET Core-аутентификация.
    app.MapHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = [new HangfireBasicAuthFilter(app.Services.GetRequiredService<IOptions<DevToolsOptions>>())],
    }).AllowAnonymous();
}

// --- /dev/* — служебные эндпоинты без аутентификации по построению (ручной прогон Hangfire-джоб,
// --- просмотр вёрстки писем). DevTools:DevEndpointsEnabled, независимо от AdminUiEnabled —
// --- на VPS всегда false (см. деплой-план), локально включается вместе с DevAuthEnabled.
if (devTools.DevEndpointsEnabled)
{
    // Ручной запуск джобы оповещений без ожидания cron/UI дашборда — для локальной проверки.
    app.MapPost("/dev/trigger-reminder-scan", async (ReminderScanJob job, CancellationToken ct) =>
    {
        await job.RunAsync(ct);
        return Results.Ok();
    });

    // /dev/trigger-outbox-dispatch удалён (ADR-0006): у MassTransit нет поддерживаемого API
    // "прогнать доставку сейчас" — UseBusOutbox будит delivery service сразу после SaveChanges,
    // иначе полинг по Messaging:Outbox:QueryDelay. Тесты/дев — на QueryDelay + WaitForAsync-полинг.

    // Синхронный прогон конкретной задачи обогащения справочника (этап 4) — минуя очередь
    // Hangfire, для локальной проверки конвейера без ожидания воркера enrichment-server.
    app.MapPost("/dev/trigger-enrichment/{jobId:guid}", async (
        Guid jobId, MedicationEnrichmentProcessor processor, CancellationToken ct) =>
    {
        await processor.RunAsync(jobId, ct);
        return Results.Ok();
    });

    // Просмотр вёрстки email-писем в браузере: LoggingEmailSender печатает в лог только
    // текстовую часть, а SMTP в dev обычно не настроен, поэтому иначе HTML не увидеть без
    // EmailPreviewWriter (юнит-тест, пишущий файлы). Правка шаблона .html требует пересборки
    // API — они embedded-ресурсы (см. EmailTemplateRenderer). AllowAnonymous обязателен:
    // FallbackPolicy выше требует аутентификации, а у браузера при заходе сюда напрямую нет
    // ни Telegram initData, ни dev-заголовка (тот же случай, что и /hangfire выше).
    app.MapGet("/dev/email-preview/{name}", (string name, EmailTemplateRenderer renderer) =>
    {
        const string demoEmail = "demo@example.com";
        string? html = name switch
        {
            "temporary-password" => renderer.RenderTemporaryPassword(
                TelegramBindingService.TemporaryPasswordCopy(demoEmail), demoEmail, "Kd7mQx4Ttb2z"),
            _ when Enum.TryParse<EmailCodePurpose>(name, ignoreCase: true, out var purpose) =>
                renderer.RenderCode(EmailOtpService.CopyFor(purpose).Copy, "482915", 10),
            _ => null,
        };
        return html is null
            ? Results.NotFound("Доступные имена: register | linkemail | resetpassword | telegrambind | temporary-password")
            : Results.Content(html, "text/html; charset=utf-8");
    }).AllowAnonymous();
}

// --- Регистрация ежедневной джобы оповещений (этап 3 п.10) ---
// Через DI (IRecurringJobManager), а не статический RecurringJob.AddOrUpdate: последний
// читает JobStorage.Current, который AddHangfire больше не выставляет автоматически.
var notificationOptions = app.Services.GetRequiredService<IOptions<NotificationOptions>>().Value;
app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<ReminderScanJob>(
    "reminder-scan",
    job => job.RunAsync(CancellationToken.None),
    notificationOptions.Cron);

// Ретеншн аудита (задача 2.7): ежемесячно, 1-го числа в 03:00 — строки старше 12 месяцев.
app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<AuditRetentionJob>(
    "audit-retention",
    job => job.RunAsync(CancellationToken.None),
    "0 3 1 * *");


// Применение миграций с retry для transient-ошибок при старте (race-condition нескольких реплик)
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync();
                break;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                logger.LogWarning(ex,
                    "Применение миграций не удалось (попытка {Attempt}/{Max}), повтор через {Delay}s",
                    attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay);
            }
        }
    }



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
