using FamilyHub.Api.Features.Bot;
using FamilyHub.Api.Features.Families;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Api.Features.Members;
using FamilyHub.Infrastructure.Auth;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Api.Features.Notifications;
using FamilyHub.Infrastructure.CurrentUser;
using FamilyHub.Infrastructure.Notifications;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Storage;
using FamilyHub.Infrastructure.Telegram;
using FamilyHub.Modules.Birthdays;
using FamilyHub.Modules.Medical;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Minio;
using Serilog;
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

// --- Persistence ---
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Postgres.")));

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

// --- Аутентификация: Telegram Mini App (прод) + Dev-заглушка (только Development) ---
var isDevelopment = builder.Environment.IsDevelopment();
var defaultScheme = isDevelopment ? "Smart" : AuthSchemes.TelegramMiniApp;

var authBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = defaultScheme;
    options.DefaultAuthenticateScheme = defaultScheme;
    options.DefaultChallengeScheme = defaultScheme;
});

authBuilder.AddScheme<AuthenticationSchemeOptions, TelegramMiniAppAuthenticationHandler>(AuthSchemes.TelegramMiniApp, null);

if (isDevelopment)
{
    authBuilder.AddScheme<AuthenticationSchemeOptions, DevAuthenticationHandler>(AuthSchemes.Dev, null);

    // Выбор схемы по заголовку: если пришёл X-Dev-TelegramId — используем Dev,
    // иначе обычную Telegram Mini App initData. Только для локальной разработки.
    authBuilder.AddPolicyScheme("Smart", "Smart", policyOptions =>
    {
        policyOptions.ForwardDefaultSelector = httpContext =>
            httpContext.Request.Headers.ContainsKey("X-Dev-TelegramId")
                ? AuthSchemes.Dev
                : AuthSchemes.TelegramMiniApp;
    });
}

// --- Оповещения: Hangfire recurring job по срокам годности лекарств и дням рождения (этап 3 п.10) ---
var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Postgres.");
builder.Services.AddHangfire(cfg => cfg.UsePostgreSqlStorage(opt => opt.UseNpgsqlConnection(postgresConnectionString)));
builder.Services.AddHangfireServer();
builder.Services.AddScoped<ReminderScanJob>();
builder.Services.AddScoped<NotificationService>();

// --- Telegram-бот: тонкий клиент + доставка оповещений (этап 4 п.12) ---
// Всё, что зависит от ITelegramBotClient, регистрируем только если задан BotToken: без него
// (локальный dev) бота не существует — нет смысла поднимать вебхук-эндпоинт и обработчик,
// а доставка оповещений остаётся в LoggingNotificationSender, как и раньше.
var telegramBotToken = builder.Configuration["Telegram:BotToken"];
var telegramBotConfigured = !string.IsNullOrWhiteSpace(telegramBotToken);
if (telegramBotConfigured)
{
    builder.Services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(telegramBotToken!));
    builder.Services.AddScoped<INotificationSender, TelegramNotificationSender>();
    builder.Services.AddScoped<TelegramUpdateHandler>();
    builder.Services.AddHostedService<TelegramWebhookRegistrar>();
}
else
{
    builder.Services.AddScoped<INotificationSender, LoggingNotificationSender>();
}

// --- Medical-модуль ---
builder.Services.AddMedicalModule();

// --- Birthdays-модуль (этап 4 п.11) ---
builder.Services.AddBirthdayModule();

// --- Swagger (ручное тестирование) ---
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// --- Структурированное логирование HTTP-запросов (метод, путь, статус, время) в Seq ---
app.UseSerilogRequestLogging();

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

// --- Раздача файлов LocalFileStorage по подписанной ссылке (только при FileStorage:Provider=Local) ---
if (string.Equals(fileStorageProvider, "Minio", StringComparison.OrdinalIgnoreCase) is false)
{
    app.MapGet("/local-files/{*key}", (string key, long expires, string sig, LocalFileStorage storage) =>
    {
        if (!storage.IsValidSignature(key, expires, sig))
            return Results.Unauthorized();

        var path = storage.ResolvePath(key);
        return File.Exists(path) ? Results.File(path) : Results.NotFound();
    }).AllowAnonymous();
}

app.MapFamilyEndpoints();
app.MapInviteEndpoints();
app.MapMemberEndpoints();
app.MapMedicalModule();
app.MapBirthdayModule();
app.MapNotificationEndpoints();
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
    app.MapHangfireDashboard("/hangfire");

    // Ручной запуск джобы оповещений без ожидания cron/UI дашборда — для локальной проверки.
    app.MapPost("/dev/trigger-reminder-scan", async (ReminderScanJob job, CancellationToken ct) =>
    {
        await job.RunAsync(ct);
        return Results.Ok();
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
