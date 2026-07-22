using FamilyHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Поднимает приложение целиком (как в проде) против реального Postgres в Testcontainers:
/// тот же DI-граф, та же Npgsql/Hangfire-инициализация, реальные EF Core миграции. Auth — через
/// уже существующий Dev-хендлер (Development => "Smart"-схема, заголовок X-Dev-TelegramId),
/// никакой подмены конвейера аутентификации/авторизации.
/// </summary>
public class FamilyHubWebFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("familyhub_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly string _uploadsRoot = Path.Combine(Path.GetTempPath(), "familyhub-it-uploads-" + Guid.NewGuid());

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Прогоняем реальные миграции один раз против поднятого контейнера — до первого запроса,
        // независимо от хоста (используем отдельный, временный AppDbContext).
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_postgres.GetConnectionString());
        await using var db = new AppDbContext(optionsBuilder.Options);
        await db.Database.MigrateAsync();
    }

    public new async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        if (Directory.Exists(_uploadsRoot))
            Directory.Delete(_uploadsRoot, recursive: true);
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // ВАЖНО: ConfigureAppConfiguration здесь не работает надёжно для top-level statements
        // (WebApplication.CreateBuilder) — добавленные так провайдеры не успевают попасть в
        // builder.Configuration, который Program.cs читает синхронно при старте (Hangfire конкретно
        // подключался к дефолтному localhost:5432 из appsettings, а не к Testcontainers-порту).
        // UseSetting пишет напрямую в тот же конфиг, который видит WebApplicationBuilder.
        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        builder.UseSetting("FileStorage:Provider", "Local");
        builder.UseSetting("LocalFileStorage:RootPath", _uploadsRoot);
        // Без BotToken: вебхук-эндпоинт и ITelegramBotClient не регистрируются (см. Program.cs) —
        // достаточно для всего, кроме BotWebhookTests, у которых своя фабрика-наследник.
        builder.UseSetting("Telegram:BotToken", "");
        // Ускоренный цикл outbox-диспетчера: тесты, проверяющие фоновую доставку, не ждут 5 секунд.
        builder.UseSetting("Outbox:PollInterval", "00:00:00.500");

        builder.ConfigureServices(services => services.AddSingleton(_postgres));
    }

    protected override Microsoft.Extensions.Hosting.IHost CreateHost(Microsoft.Extensions.Hosting.IHostBuilder builder)
    {
        // См. HostCreationSync: параллельные коллекции не должны строить Program одновременно.
        lock (HostCreationSync.Lock)
        {
            return base.CreateHost(builder);
        }
    }

    /// <summary>HttpClient, аутентифицированный как dev-пользователь с данным TelegramId (см. DevAuthenticationHandler).</summary>
    public HttpClient CreateClientAs(long telegramId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-TelegramId", telegramId.ToString());
        return client;
    }
}
