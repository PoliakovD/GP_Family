using FamilyHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Minio;
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

    // MinIO — теперь единственная реализация IFileStorage (LocalFileStorage упразднён), поэтому
    // вложения гоняются через реальный объектный стор и здесь, а не через временный каталог на диске.
    private readonly MinioContainer _minio = new MinioBuilder()
        .WithUsername("minioadmin")
        .WithPassword("minioadmin")
        .Build();

    public virtual async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _minio.StartAsync());

        // Прогоняем реальные миграции один раз против поднятого контейнера — до первого запроса,
        // независимо от хоста (используем отдельный, временный AppDbContext).
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_postgres.GetConnectionString());
        await using var db = new AppDbContext(optionsBuilder.Options, DesignTimeDbContextFactory.CreateDevCipher());
        await db.Database.MigrateAsync();
    }

    // virtual (не только new) — KafkaWebFactory должна попасть в вызов через override, иначе
    // xUnit (диспетчеризующий через интерфейс IAsyncLifetime) вызвал бы ТОЛЬКО этот метод, минуя
    // _kafka.DisposeAsync() в производном классе (new без virtual не переопределяет слот интерфейса).
    public new virtual async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _minio.DisposeAsync();
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
        // GetConnectionString() отдаёт полный URL ("http://127.0.0.1:PORT/") — Minio:Endpoint
        // ждёт голый host:port (см. MinioFileStorage: .WithEndpoint(...).WithSSL(...) раздельно).
        builder.UseSetting("Minio:Endpoint", new Uri(_minio.GetConnectionString()).Authority);
        builder.UseSetting("Minio:AccessKey", _minio.GetAccessKey());
        builder.UseSetting("Minio:SecretKey", _minio.GetSecretKey());
        builder.UseSetting("Minio:UseSsl", "false");
        // Секреты не хардкодятся в appsettings.Development.json (даже для dev — см. Program.cs
        // fail-fast) — тестовый хост задаёт свои фиксированные значения явно, тем же путём, что и
        // остальную конфигурацию здесь. DevMasterKey переиспользует константу, которой уже
        // пользуется InitializeAsync выше для миграций (design-time-ключ, не для прода).
        builder.UseSetting("Encryption:MasterKey", DesignTimeDbContextFactory.DevMasterKey);
        builder.UseSetting("Jwt:SigningKey", "ZGV2LWp3dC1zaWduaW5nLWtleS0zMi1ieXRlcy1va2s=");
        builder.UseSetting("Attachments:DownloadSigningKey", "dev-attachment-download-signing-key");
        // Без BotToken: вебхук-эндпоинт и ITelegramBotClient не регистрируются (см. Program.cs) —
        // достаточно для всего, кроме BotWebhookTests, у которых своя фабрика-наследник.
        builder.UseSetting("Telegram:BotToken", "");
        // Ускоренный цикл EF Core Outbox delivery service: тесты, проверяющие фоновую доставку,
        // не ждут дефолтные несколько секунд (см. ADR-0006, MassTransitRegistration). 500мс, не
        // меньше (тот же интервал, что был у старого Outbox:PollInterval) — каждый тик делает
        // несколько запросов (OutboxState lock+select, InboxState cleanup, OutboxMessage select),
        // тяжелее одного простого select старого OutboxDispatcher; при ~15 одновременно живых
        // WebFactory-коллекциях в интеграционном прогоне более агрессивный интервал (пробовали
        // 200мс) ощутимо нагружал Postgres-контейнеры и вызывал транзитные обрывы соединений
        // (Npgsql "connection forcibly closed") на части тестов — не логическая ошибка, а
        // эмпирически найденный порог для этого окружения.
        builder.UseSetting("Messaging:Outbox:QueryDelay", "00:00:00.500");
        // Дефолт для всей коллекции — без брокера; KafkaWebFactory переопределяет на true.
        builder.UseSetting("Messaging:Kafka:Enabled", "false");
        // Все тесты коллекции ходят с одного IP: штатные лимиты дали бы ложные 429.
        // Тест брутфорс-защиты использует отдельную фабрику с заниженными лимитами.
        builder.UseSetting("RateLimiting:AuthPermitLimit", "100000");
        builder.UseSetting("RateLimiting:CodePermitLimit", "100000");
        builder.UseSetting("RateLimiting:RedeemPermitLimit", "100000");
        // Детерминированный домен в письмах: тесты, проверяющие HTML/CTA, не зависят от appsettings.
        builder.UseSetting("Email:PublicSiteUrl", "https://test.familyhub.local");

        builder.ConfigureServices(services =>
        {
            services.AddSingleton(_postgres);
            services.AddSingleton(_minio);
            // Перехват писем (коды PWA-регистрации): регистрация ПОСЛЕ Program.cs — выигрывает.
            services.AddSingleton<CapturingEmailSender>();
            services.AddSingleton<FamilyHub.Infrastructure.Email.IEmailSender>(
                sp => sp.GetRequiredService<CapturingEmailSender>());
        });
    }

    /// <summary>Доступ к перехваченным письмам (коды подтверждения) для тестов.</summary>
    public CapturingEmailSender Emails => Services.GetRequiredService<CapturingEmailSender>();

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
