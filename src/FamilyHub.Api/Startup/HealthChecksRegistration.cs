using FamilyHub.Api.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — health checks (нужны и для деплой-пайплайна,
/// и для docker-compose depends_on: service_healthy), без изменения поведения/порядка. Add- и
/// Map-часть в одном файле (в отличие от большинства других Startup-файлов) — сама фича маленькая
/// и цельная, разносить её по build-фазе/pipeline-фазе добавило бы косвенности без пользы.
/// </summary>
public static class HealthChecksRegistration
{
    /// <summary>Встроенная проверка MassTransit (8.x регистрирует её сама с тегами "ready"/"masstransit").</summary>
    public const string BusCheckName = "masstransit-bus";

    /// <summary>Окно после старта процесса, в течение которого шина ещё может законно стартовать.
    /// Kestrel начинает отвечать РАНЬШЕ, чем MassTransit поднимет шину (особенно Kafka Rider: вход в
    /// consumer group, а после нечистой остановки брокер ещё ждёт session.timeout.ms «мёртвого»
    /// члена группы) — без окна /health/ready отвечал бы 503 «Not ready: not started» на каждом
    /// старте, хотя API уже может работать: события пишутся в EF-outbox и уходят в шину позже.</summary>
    public static readonly TimeSpan BusStartupGrace = TimeSpan.FromMinutes(2);

    private static DateTime _startedAtUtc = DateTime.UtcNow;

    /// <summary>Входит ли проверка в /health/ready. В окне прогрева проверка шины пропускается,
    /// дальше — обычная: шина, так и не стартовавшая за BusStartupGrace, честно даёт 503.</summary>
    public static bool IsReadyCheck(HealthCheckRegistration check, TimeSpan sinceStart) =>
        check.Tags.Contains("ready") && !(check.Name == BusCheckName && sinceStart < BusStartupGrace);

    public static WebApplicationBuilder AddFamilyHubHealthChecks(this WebApplicationBuilder builder)
    {
        _startedAtUtc = DateTime.UtcNow;

        // "llm" — отдельный тег: LM Studio на ноутбуке пользователя за WireGuard, его
        // недоступность (сон/выключен) ожидаема и не должна валить общую готовность (тег "ready").
        builder.Services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"])
            .AddCheck<MinioHealthCheck>("minio", tags: ["ready"])
            .AddCheck<KafkaHealthCheck>("kafka", tags: ["ready"])
            .AddCheck<LmStudioHealthCheck>("llm", tags: ["llm"]);

        return builder;
    }

    /// <summary>/health/live — процесс жив (без проверок, для liveness-проб); /health/ready —
    /// зависимости на месте (Postgres/MinIO/Kafka, тег "ready"); /health/llm — отдельно, т.к. LM
    /// Studio на ноутбуке за WireGuard и его недоступность не должна валить readiness всего контура.</summary>
    public static WebApplication MapFamilyHubHealthChecks(this WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => IsReadyCheck(check, DateTime.UtcNow - _startedAtUtc),
        }).AllowAnonymous();
        app.MapHealthChecks("/health/llm", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("llm"),
        }).AllowAnonymous();

        return app;
    }
}
