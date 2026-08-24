using FamilyHub.TelegramBot.Api;
using FamilyHub.TelegramBot.Configuration;
using FamilyHub.TelegramBot.Health;
using FamilyHub.TelegramBot.Messaging;
using FamilyHub.TelegramBot.Webhook;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Serilog.Exceptions;
using Telegram.Bot;

// Тот же приём, что в FamilyHub.Api: bootstrap-логгер ловит ошибки, случившиеся до того, как
// builder.Build() поднимет настоящий Serilog-логгер из конфигурации.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Запуск FamilyHub.TelegramBot...");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithExceptionDetails()
        .Enrich.WithEnvironmentName()
        .Enrich.WithMachineName());

    // См. тот же комментарий в FamilyHub.Api/Program.cs — этот процесс тоже держит Kafka Rider
    // (BotMessagingRegistration), дефолтных 5с ShutdownTimeout не хватает на graceful LeaveGroup.
    builder.Host.ConfigureHostOptions(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

    builder.Services.Configure<BotOptions>(builder.Configuration.GetSection(BotOptions.SectionName));
    builder.Services.Configure<FamilyHubApiOptions>(builder.Configuration.GetSection(FamilyHubApiOptions.SectionName));
    builder.Services.Configure<InternalApiOptions>(builder.Configuration.GetSection(InternalApiOptions.SectionName));

    // --- Клиент /internal/bot/* на FamilyHub.Api — заменяет прямой доступ к БД, которого у ---
    // --- бота нет (см. IFamilyHubApiClient) ---
    builder.Services.AddTransient<InternalTokenHandler>();
    builder.Services.AddHttpClient<IFamilyHubApiClient, FamilyHubApiClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<FamilyHubApiOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.BaseUrl))
                throw new InvalidOperationException(
                    "FamilyHubApi:BaseUrl не задан (env FamilyHubApi__BaseUrl) — бот не может достучаться до /internal/bot/*.");
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        })
        .AddHttpMessageHandler<InternalTokenHandler>();

    // --- Telegram-клиент и вебхук: только если задан BotToken (без него — локальный dev без ---
    // --- контейнера бота, тот же fail-soft идиом, что раньше был в FamilyHub.Api) ---
    var telegramBotToken = builder.Configuration["Telegram:BotToken"];
    var telegramBotConfigured = !string.IsNullOrWhiteSpace(telegramBotToken);
    if (telegramBotConfigured)
    {
        builder.Services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(telegramBotToken!));
        builder.Services.AddScoped<TelegramUpdateHandler>();
        builder.Services.AddHostedService<TelegramWebhookRegistrar>();
    }

    // --- Шина: consumer-only, без EF outbox (см. BotMessagingRegistration) ---
    builder.Services.AddBotMessaging(builder.Configuration);

    // --- Health checks: "ready" — то, что бот способен принимать вебхук и достучаться до ---
    // --- Kafka/Api БЕЗ туннеля; "telegram" — отдельно, доказывает именно egress через туннель ---
    // --- (см. TelegramApiHealthCheck) — недоступность Telegram не должна валить readiness ---
    builder.Services.AddHealthChecks()
        .AddCheck<KafkaHealthCheck>("kafka", tags: ["ready"])
        .AddCheck<TelegramApiHealthCheck>("telegram", tags: ["telegram"]);

    var app = builder.Build();

    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} -> {StatusCode} за {Elapsed:0.0}мс";
        options.GetLevel = (httpContext, elapsed, ex) => ex is not null
            ? LogEventLevel.Error
            : httpContext.Response.StatusCode >= 500 ? LogEventLevel.Error
            : httpContext.Response.StatusCode >= 400 ? LogEventLevel.Warning
            : httpContext.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Debug
            : LogEventLevel.Information;
    });

    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
    }).AllowAnonymous();
    app.MapHealthChecks("/health/telegram", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("telegram"),
    }).AllowAnonymous();

    if (telegramBotConfigured)
    {
        app.MapBotEndpoints();
    }

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    // HostAbortedException прилетает от design-time сборки хоста (WebApplicationFactory
    // в интеграционных тестах) — это не сбой.
    Log.Fatal(ex, "FamilyHub.TelegramBot аварийно завершился при запуске");
}
finally
{
    Log.CloseAndFlush();
}

// Сгенерированный для top-level statements класс Program по умолчанию internal — для
// WebApplicationFactory<Program> в интеграционных тестах нужен public (тот же приём, что в FamilyHub.Api).
public partial class Program { }
