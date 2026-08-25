extern alias TelegramBotHost;

using System.Net.Http;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Telegram.Bot;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;
using Xunit;
using TelegramBotProgram = TelegramBotHost::Program;
using BotOptions = TelegramBotHost::FamilyHub.TelegramBot.Configuration.BotOptions;
using FamilyHubApiOptions = TelegramBotHost::FamilyHub.TelegramBot.Configuration.FamilyHubApiOptions;
using InternalApiOptions = TelegramBotHost::FamilyHub.TelegramBot.Configuration.InternalApiOptions;
using IFamilyHubApiClient = TelegramBotHost::FamilyHub.TelegramBot.Api.IFamilyHubApiClient;
using FamilyHubApiClient = TelegramBotHost::FamilyHub.TelegramBot.Api.FamilyHubApiClient;
using InternalTokenHandler = TelegramBotHost::FamilyHub.TelegramBot.Api.InternalTokenHandler;

namespace FamilyHub.IntegrationTests.Bot;

/// <summary>
/// Сквозной двухпроцессный тест-фикстур (ADR-0008): после выноса бота в отдельный сервис
/// /bot/webhook живёт в FamilyHub.TelegramBot, а InviteService/TelegramLinkService/
/// IUserProvisioningService — по-прежнему в FamilyHub.Api за /internal/bot/*. Вместо реального
/// сетевого сокета между двумя WebApplicationFactory связываем их напрямую через
/// TestServer.CreateHandler() — тот же приём, каким Microsoft рекомендует тестировать
/// межсервисные интеграции без реального HTTP-порта. extern alias TelegramBotHost обязателен:
/// у FamilyHub.Api и FamilyHub.TelegramBot одноимённый top-level Program в глобальном
/// пространстве имён (см. .csproj).
/// </summary>
public class BotIntegrationFixture : IAsyncLifetime
{
    public const string WebhookSecret = "test-webhook-secret";
    public const string InternalToken = "test-internal-bot-api-token-0123456789abcdef";
    public const string BotToken = "test-bot-token-not-real";

    public ApiForBotTestsFactory ApiFactory { get; } = new();

    private BotHostFactory? _botFactory;

    public ITelegramBotClient BotClient { get; } = Substitute.For<ITelegramBotClient>();

    public async Task InitializeAsync()
    {
        await ApiFactory.InitializeAsync();
        // Форсируем построение хоста Api ДО создания фабрики бота — IFamilyHubApiClient бота
        // резолвится через ApiFactory.Server.CreateHandler(), которому нужен уже поднятый TestServer.
        _ = ApiFactory.Server;

        _botFactory = new BotHostFactory(ApiFactory, BotClient);
    }

    public async Task DisposeAsync()
    {
        if (_botFactory is not null)
            await _botFactory.DisposeAsync();
        await ApiFactory.DisposeAsync();
    }

    /// <summary>HttpClient против /bot/webhook (хост FamilyHub.TelegramBot).</summary>
    public HttpClient CreateBotWebhookClient() => _botFactory!.CreateClient();

    /// <summary>HttpClient против /api/* (хост FamilyHub.Api) — те же helpers, что у остальных интеграционных тестов.</summary>
    public HttpClient CreateApiClient() => ApiFactory.CreateClient();

    public IServiceProvider ApiServices => ApiFactory.Services;

    private class BotHostFactory(ApiForBotTestsFactory apiFactory, ITelegramBotClient botClient)
        : WebApplicationFactory<TelegramBotProgram>
    {
        protected override Microsoft.Extensions.Hosting.IHost CreateHost(Microsoft.Extensions.Hosting.IHostBuilder builder)
        {
            // См. HostCreationSync: тот же общий лок, что у Api-фабрик — гонка HostFactoryResolver
            // не завязана на конкретный entry point, а на статический DiagnosticListener процесса.
            lock (HostCreationSync.Lock)
            {
                return base.CreateHost(builder);
            }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            builder.UseSetting("Telegram:BotToken", BotIntegrationFixture.BotToken);
            builder.UseSetting("Telegram:WebhookSecret", BotIntegrationFixture.WebhookSecret);
            // Пусто — TelegramWebhookRegistrar.StartAsync пропускает SetWebhook, ничего не
            // уходит в реальный Telegram при старте тестового хоста.
            builder.UseSetting("Telegram:WebhookUrl", "");
            builder.UseSetting("Telegram:MiniAppUrl", "https://mini.example.test");
            // BaseUrl формально нужен только если бы IFamilyHubApiClient резолвился через
            // штатный AddHttpClient — здесь он полностью подменяется ниже, значение никогда не
            // используется для реального DNS/сетевого вызова.
            builder.UseSetting("FamilyHubApi:BaseUrl", "http://api-testserver.invalid");
            builder.UseSetting("Internal:BotApiToken", BotIntegrationFixture.InternalToken);
            // Без брокера: этот фикстур покрывает вебхук/внутренний API, не Kafka-доставку
            // (см. KafkaBridgeFlowTests на стороне Api — telegram-outbound топик тестируется там).
            builder.UseSetting("Messaging:Kafka:Enabled", "false");

            builder.ConfigureServices(services =>
            {
                services.AddSingleton(botClient);

                // Подменяем IFamilyHubApiClient целиком: вместо IHttpClientFactory с реальным
                // сокетом — HttpClient поверх TestServer.CreateHandler() Api-хоста. Последняя
                // регистрация в контейнере выигрывает (тот же приём, что и AddSingleton(botClient)
                // выше поверх Program.cs).
                services.AddSingleton<IFamilyHubApiClient>(sp =>
                {
                    var tokenHandler = new InternalTokenHandler(sp.GetRequiredService<IOptions<InternalApiOptions>>())
                    {
                        InnerHandler = apiFactory.Server.CreateHandler(),
                    };
                    var http = new HttpClient(tokenHandler) { BaseAddress = new Uri("http://api-testserver.invalid") };
                    return new FamilyHubApiClient(http);
                });
            });
        }
    }
}

/// <summary>
/// Api-хост для двухпроцессных бот-тестов — тот же контейнерный стек, что FamilyHubWebFactory,
/// плюс Internal:BotApiToken (см. InternalBotAuthFilter). BotToken НЕ задаём: initData HMAC не
/// нужен ни одному тесту этой коллекции, а /internal/bot/* гейтится отдельным флагом
/// (internalBotApiConfigured в Program.cs), независимым от BotToken.
/// </summary>
public class ApiForBotTestsFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("familyhub_bot_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly MinioContainer _minio = new MinioBuilder()
        .WithUsername("minioadmin")
        .WithPassword("minioadmin")
        .Build();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _minio.StartAsync());

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_postgres.GetConnectionString());
        await using var db = new AppDbContext(optionsBuilder.Options, DesignTimeDbContextFactory.CreateDevCipher());
        await db.Database.MigrateAsync();
    }

    // Хост должен полностью остановиться ПЕРВЫМ, контейнеры — только после (см. подробное
    // объяснение в FamilyHubWebFactory.DisposeAsync(), тот же баг был продублирован сюда
    // независимо): пока хост жив, MassTransit-фоновые сервисы продолжают опрашивать Postgres
    // по таймеру и ловят "Connection refused" на уже убитый Testcontainers-порт.
    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _minio.DisposeAsync();
    }

    protected override Microsoft.Extensions.Hosting.IHost CreateHost(Microsoft.Extensions.Hosting.IHostBuilder builder)
    {
        lock (HostCreationSync.Lock)
        {
            return base.CreateHost(builder);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        builder.UseSetting("Minio:Endpoint", new Uri(_minio.GetConnectionString()).Authority);
        builder.UseSetting("Minio:AccessKey", _minio.GetAccessKey());
        builder.UseSetting("Minio:SecretKey", _minio.GetSecretKey());
        builder.UseSetting("Minio:UseSsl", "false");
        builder.UseSetting("Encryption:MasterKey", DesignTimeDbContextFactory.DevMasterKey);
        builder.UseSetting("Jwt:SigningKey", "ZGV2LWp3dC1zaWduaW5nLWtleS0zMi1ieXRlcy1va2s=");
        builder.UseSetting("Attachments:DownloadSigningKey", "dev-attachment-download-signing-key");
        builder.UseSetting("Telegram:BotUsername", "familyhub_test_bot");
        builder.UseSetting("Internal:BotApiToken", BotIntegrationFixture.InternalToken);
        builder.UseSetting("DevTools:DevAuthEnabled", "true");
        builder.UseSetting("RateLimiting:AuthPermitLimit", "100000");
        builder.UseSetting("RateLimiting:CodePermitLimit", "100000");
        builder.UseSetting("RateLimiting:RedeemPermitLimit", "100000");

        builder.ConfigureServices(services =>
        {
            services.AddSingleton(_postgres);
            services.AddSingleton(_minio);
        });
    }
}

[CollectionDefinition(Name)]
public class BotIntegrationCollection : ICollectionFixture<BotIntegrationFixture>
{
    public const string Name = "FamilyHub bot integration tests";
}
