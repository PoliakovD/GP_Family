using FamilyHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Testcontainers.PostgreSql;
using Xunit;

namespace FamilyHub.IntegrationTests.Bot;

/// <summary>
/// Отдельная от <see cref="FamilyHubWebFactory"/> фабрика: только тут BotToken непустой, чтобы
/// Program.cs замапил /bot/webhook и поднял TelegramUpdateHandler/TelegramNotificationSender.
/// ITelegramBotClient подменяем NSubstitute-моком ПОСЛЕ регистрации Program.cs (DI — последняя
/// регистрация выигрывает), чтобы хендлер не пытался реально стучаться в Telegram API.
/// WebhookUrl оставляем пустым — TelegramWebhookRegistrar.StartAsync тогда пропускает SetWebhook.
/// </summary>
public class BotWebhookWebFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string WebhookSecret = "test-webhook-secret";

    public ITelegramBotClient BotClient { get; } = Substitute.For<ITelegramBotClient>();

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("familyhub_bot_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly string _uploadsRoot = Path.Combine(Path.GetTempPath(), "familyhub-it-bot-uploads-" + Guid.NewGuid());

    public BotWebhookWebFactory()
    {
        BotClient.SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>()).Returns(new Message());
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_postgres.GetConnectionString());
        await using var db = new AppDbContext(optionsBuilder.Options, DesignTimeDbContextFactory.CreateDevCipher());
        await db.Database.MigrateAsync();
    }

    public new async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        if (Directory.Exists(_uploadsRoot))
            Directory.Delete(_uploadsRoot, recursive: true);
        await base.DisposeAsync();
    }

    protected override Microsoft.Extensions.Hosting.IHost CreateHost(Microsoft.Extensions.Hosting.IHostBuilder builder)
    {
        // См. HostCreationSync: параллельные коллекции не должны строить Program одновременно.
        lock (HostCreationSync.Lock)
        {
            return base.CreateHost(builder);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        builder.UseSetting("FileStorage:Provider", "Local");
        builder.UseSetting("LocalFileStorage:RootPath", _uploadsRoot);
        // Секреты не хардкодятся в appsettings.Development.json (даже для dev — см. Program.cs
        // fail-fast) — тестовый хост задаёт свои фиксированные значения явно.
        builder.UseSetting("Encryption:MasterKey", DesignTimeDbContextFactory.DevMasterKey);
        builder.UseSetting("Jwt:SigningKey", "ZGV2LWp3dC1zaWduaW5nLWtleS0zMi1ieXRlcy1va2s=");
        builder.UseSetting("Attachments:DownloadSigningKey", "dev-attachment-download-signing-key");
        builder.UseSetting("Telegram:BotToken", "test-bot-token-not-real");
        builder.UseSetting("Telegram:BotUsername", "familyhub_test_bot");
        builder.UseSetting("Telegram:WebhookSecret", WebhookSecret);
        builder.UseSetting("Telegram:WebhookUrl", "");
        // Тесты линковки Telegram (POST /api/auth/link-telegram/start) идут с одного IP —
        // штатный лимит "auth-code" (3/час) дал бы ложные 429 в наборе из нескольких тестов.
        builder.UseSetting("RateLimiting:AuthPermitLimit", "100000");
        builder.UseSetting("RateLimiting:CodePermitLimit", "100000");
        builder.UseSetting("RateLimiting:RedeemPermitLimit", "100000");

        builder.ConfigureServices(services =>
        {
            services.AddSingleton(_postgres);
            services.AddSingleton(BotClient);
        });
    }
}
