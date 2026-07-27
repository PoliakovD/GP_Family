using System.Threading.RateLimiting;
using FamilyHub.Api.Features.Auth;
using FamilyHub.Api.Features.Account;
using FamilyHub.Api.Features.Bot;
using FamilyHub.Api.Features.Consents;
using FamilyHub.Api.Features.Families;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Api.Features.Members;
using FamilyHub.Infrastructure.Audit;
using FamilyHub.Infrastructure.Auth;
using FamilyHub.Infrastructure.Auth.Jwt;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Consents;
using FamilyHub.Api.Features.Notifications;
using FamilyHub.Api.Features.Push;
using FamilyHub.Infrastructure.CurrentUser;
using FamilyHub.Infrastructure.Email;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Notifications;
using FamilyHub.Infrastructure.Outbox;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Security;
using FamilyHub.Infrastructure.Storage;
using FamilyHub.Infrastructure.Telegram;
using FamilyHub.Modules.Birthdays;
using FamilyHub.Modules.Medical;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Minio;
using Serilog;
using Serilog.Events;
using Serilog.Exceptions;
using Telegram.Bot;

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
builder.Services.Configure<LocalFileStorageOptions>(builder.Configuration.GetSection(LocalFileStorageOptions.SectionName));
builder.Services.Configure<MinioOptions>(builder.Configuration.GetSection(MinioOptions.SectionName));
builder.Services.Configure<NotificationOptions>(builder.Configuration.GetSection(NotificationOptions.SectionName));
builder.Services.Configure<LmStudioOptions>(builder.Configuration.GetSection(LmStudioOptions.SectionName));
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection(OutboxOptions.SectionName));
builder.Services.Configure<EncryptionOptions>(builder.Configuration.GetSection(EncryptionOptions.SectionName));
builder.Services.Configure<AttachmentDownloadOptions>(builder.Configuration.GetSection(AttachmentDownloadOptions.SectionName));
builder.Services.Configure<ConsentOptions>(builder.Configuration.GetSection(ConsentOptions.SectionName));
builder.Services.Configure<WebPushOptions>(builder.Configuration.GetSection(WebPushOptions.SectionName));
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));

// --- At-rest шифрование (этап 2, 152-ФЗ): ключ вне БД, fail-fast при отсутствии ---
// Синглтоны обязательны: EF кэширует модель с конвертером, захватившим первый cipher.
if (string.IsNullOrWhiteSpace(builder.Configuration["Encryption:MasterKey"]))
    throw new InvalidOperationException(
        "Encryption:MasterKey не задан (env Encryption__MasterKey) — at-rest шифрование обязательно.");
builder.Services.AddSingleton<IFieldCipher, AesGcmFieldCipher>();
builder.Services.AddSingleton<IFileCipher, AesGcmFileCipher>();
builder.Services.AddSingleton<DownloadTokenService>();

// --- Persistence ---
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Postgres.")));

// --- Событийная шина: MediatR + транзакционный outbox (этап 1 плана) ---
// Хендлеры ищутся сканом сборок Infrastructure (Notifications-хендлеры) и модулей;
// кастомный publisher изолирует сбой отдельного хендлера от остальных.
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssemblies(
        typeof(OutboxDispatcher).Assembly,
        typeof(MedicalModule).Assembly,
        typeof(BirthdayModule).Assembly);
    cfg.NotificationPublisherType = typeof(IsolatingLoggingPublisher);
});
builder.Services.AddSingleton<EventTypeRegistry>();
builder.Services.AddScoped<IOutboxWriter, OutboxWriter>();
builder.Services.AddScoped<OutboxProcessor>();
builder.Services.AddHostedService<OutboxDispatcher>();

// --- Текущий пользователь / провижининг ---
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
builder.Services.AddScoped<IUserProvisioningService, UserProvisioningService>();

// --- Telegram auth ---
builder.Services.AddScoped<ITelegramInitDataValidator, TelegramInitDataValidator>();

// --- Хранилище файлов: переключатель FileStorage:Provider = Local|Minio (этап 2 п.9) ---
var fileStorageProvider = builder.Configuration["FileStorage:Provider"] ?? "Local";
if (string.Equals(fileStorageProvider, "Minio", StringComparison.OrdinalIgnoreCase))
{
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
}
else
{
    builder.Services.AddSingleton<LocalFileStorage>();
    builder.Services.AddSingleton<IFileStorage>(sp => sp.GetRequiredService<LocalFileStorage>());
}

// --- Core-фичи: семьи, приглашения, участники ---
builder.Services.AddScoped<FamilyService>();
builder.Services.AddScoped<InviteService>();
builder.Services.AddScoped<MembershipService>();

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

// --- Аутентификация: два окружения (этап 2 п.2.4) — Telegram Mini App и PWA-JWT,   ---
// --- плюс Dev-заглушка (только Development). Селектор "Smart" во всех средах:      ---
// --- tma-заголовок → Telegram; X-Dev-TelegramId (dev) → Dev; иначе → JWT.          ---
var isDevelopment = builder.Environment.IsDevelopment();

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

if (isDevelopment)
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
        if (isDevelopment && request.Headers.ContainsKey("X-Dev-TelegramId")) return AuthSchemes.Dev;
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
});

// --- PWA-auth сервисы + email-отправка (задача 2.5) ---
// Провайдеры заданы → MailKit с failover (российский SMTP задаётся конфигом),
// иначе — log-заглушка (зеркало переключателя INotificationSender по BotToken).
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
builder.Services.AddScoped<EmailOtpService>();
builder.Services.AddScoped<PwaAuthService>();
builder.Services.AddScoped<TelegramBindingService>();
var emailProvidersConfigured = builder.Configuration.GetSection($"{EmailOptions.SectionName}:Providers").GetChildren().Any();
if (emailProvidersConfigured)
{
    builder.Services.AddSingleton<ISmtpTransport, MailKitSmtpTransport>();
    builder.Services.AddSingleton<IEmailSender, MailKitSmtpEmailSender>();
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
builder.Services.AddHangfireServer();
builder.Services.AddScoped<ReminderScanJob>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<NotificationSendingService>();
builder.Services.AddScoped<PushSubscriptionService>();

// --- Telegram-бот: тонкий клиент + доставка оповещений (этап 4 п.12) ---
// Всё, что зависит от ITelegramBotClient, регистрируем только если задан BotToken: без него
// (локальный dev) бота не существует — нет смысла поднимать вебхук-эндпоинт и обработчик.
var telegramBotToken = builder.Configuration["Telegram:BotToken"];
var telegramBotConfigured = !string.IsNullOrWhiteSpace(telegramBotToken);
if (telegramBotConfigured)
{
    builder.Services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(telegramBotToken!));
    builder.Services.AddScoped<INotificationSender, TelegramNotificationSender>();
    builder.Services.AddScoped<TelegramUpdateHandler>();
    builder.Services.AddHostedService<TelegramWebhookRegistrar>();
}

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

// --- LM Studio: локальная vision-LLM для оцифровки медикаментов по фото (не хранит фото) ---
builder.Services.AddHttpClient<ILmStudioVisionClient, LmStudioVisionClient>((sp, client) =>
{
    var lmStudioOptions = sp.GetRequiredService<IOptions<LmStudioOptions>>().Value;
    client.BaseAddress = new Uri(lmStudioOptions.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(lmStudioOptions.TimeoutSeconds);
});

// --- Medical-модуль ---
builder.Services.AddMedicalModule();

// --- Birthdays-модуль (этап 4 п.11) ---
builder.Services.AddBirthdayModule();

// --- Swagger (ручное тестирование) ---
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// --- Структурированное логирование HTTP-запросов (метод, путь, статус, время, пользователь) в Seq ---
// Уровень поднимается на 4xx/5xx и падает на Debug для успешных запросов к статике/Hangfire —
// иначе консоль в Development захлёстывает шумом от каждого ассета SPA.
app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} -> {StatusCode} за {Elapsed:0.0}мс";

    options.GetLevel = (httpContext, elapsed, ex) => ex is not null
        ? LogEventLevel.Error
        : httpContext.Response.StatusCode >= 500 ? LogEventLevel.Error
        : httpContext.Response.StatusCode >= 400 ? LogEventLevel.Warning
        : httpContext.Request.Path.StartsWithSegments("/hangfire") ? LogEventLevel.Debug
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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// --- Раздача Telegram Mini App (React-сборка в wwwroot, этап 4 п.12) ---
// До UseAuthorization: статика отдаётся без аутентификации, сам Mini App
// аутентифицируется на уровне API-запросов через initData.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Явный запрет кэширования для всех /api-ответов. Обнаружен случай (Telegram Mini App
// WebView), когда GET /api/auth/me иногда получал закэшированный где-то на клиенте
// index.html вместо актуального JSON, хотя прямые HTTP-проверки того же бэкенда/прокси
// всегда отвечали корректно — сам ответ API не запрещал явно своё кэширование. Response
// Cache-Control — авторитетный сигнал для любого кэша (клиентского, прокси), надёжнее
// одних только запросных заголовков.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
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
app.MapMedicalModule();
app.MapBirthdayModule();
app.MapNotificationEndpoints();
app.MapPushEndpoints();
if (telegramBotConfigured)
{
    app.MapBotEndpoints();
}

// SPA-fallback для Mini App: любой нераспознанный путь отдаёт index.html (React-роутинг).
// AllowAnonymous обязателен — иначе FallbackPolicy потребует аутентификацию и до React
// дело не дойдёт даже для статических маршрутов приложения.
app.MapFallbackToFile("index.html").AllowAnonymous();

// --- Дашборд Hangfire — только Development (в проде потребовался бы отдельный auth-фильтр) ---
if (app.Environment.IsDevelopment())
{
    // AllowAnonymous обязателен: FallbackPolicy выше требует аутентификации для всех
    // эндпоинтов без явного исключения, а у браузера при заходе на /hangfire нет ни
    // Telegram initData, ни dev-заголовка X-Dev-TelegramId.
    // Authorization = [] отключает собственный фильтр Hangfire (по умолчанию
    // LocalRequestsOnlyAuthorizationFilter, который 401-ит все запросы, где
    // RemoteIpAddress не loopback — это ловит открытие дашборда через WSL/проброс портов
    // или LAN-адрес хоста, даже когда ASP.NET Core-авторизация уже пропущена).
    app.MapHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = Array.Empty<Hangfire.Dashboard.IDashboardAuthorizationFilter>(),
    }).AllowAnonymous();

    // Ручной запуск джобы оповещений без ожидания cron/UI дашборда — для локальной проверки.
    app.MapPost("/dev/trigger-reminder-scan", async (ReminderScanJob job, CancellationToken ct) =>
    {
        await job.RunAsync(ct);
        return Results.Ok();
    });

    // Синхронный прогон outbox-доставки без ожидания фонового цикла — для локальной
    // проверки и детерминизма интеграционных тестов.
    app.MapPost("/dev/trigger-outbox-dispatch", async (OutboxProcessor processor, CancellationToken ct) =>
    {
        var processed = await processor.ProcessBatchAsync(ct);
        return Results.Ok(new { processed });
    });
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
