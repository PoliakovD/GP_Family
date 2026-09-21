using Serilog;
using Serilog.Exceptions;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге (композиционный корень был 1229 строк одним
/// top-level файлом) — секция логирования без изменения поведения/порядка.
/// </summary>
public static class BootstrapLogging
{
    /// <summary>Bootstrap-логгер ловит ошибки, которые случаются до того, как builder.Build()
    /// поднимет настоящий Serilog-логгер из конфигурации (например, сбой при чтении appsettings).
    /// Вызывается ДО WebApplication.CreateBuilder — нет ещё ни builder, ни конфигурации.</summary>
    public static void ConfigureBootstrapLogger() =>
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .CreateBootstrapLogger();

    /// <summary>Настоящий Serilog поверх конфигурации + отладочный ShutdownTimeout.</summary>
    public static WebApplicationBuilder AddFamilyHubLogging(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((context, services, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithExceptionDetails()
            .Enrich.WithEnvironmentName()
            .Enrich.WithMachineName());

        // --- Отладка (частые "masstransit-bus: Not ready: not started" в Seq): дефолтный
        // --- HostOptions.ShutdownTimeout — 5 секунд, а MassTransit на graceful stop обязан ДОЖДАТЬСЯ
        // --- корректного LeaveGroup для КАЖДОЙ из 7 consumer group Kafka Rider'а (см.
        // --- MassTransitRegistration) плюс остановку EF outbox delivery service. 5с на это часто не
        // --- хватает — при редеплое/рестарте контейнера хост принудительно убивает процесс раньше, чем
        // --- клиент успевает попрощаться с группой; брокер тогда держит место за "мёртвым" консьюмером
        // --- до истечения session.timeout.ms, и КАЖДЫЙ следующий старт висит в "not started" дольше
        // --- обычного — вплоть до момента, пока Kafka не отдаст партиции новому инстансу. Значение
        // --- согласовано с stop_grace_period в deploy/docker-compose.prod.yml (должен быть больше).
        builder.Host.ConfigureHostOptions(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

        return builder;
    }
}
