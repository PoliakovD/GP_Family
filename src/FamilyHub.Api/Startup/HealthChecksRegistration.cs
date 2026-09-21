using FamilyHub.Api.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — health checks (нужны и для деплой-пайплайна,
/// и для docker-compose depends_on: service_healthy), без изменения поведения/порядка. Add- и
/// Map-часть в одном файле (в отличие от большинства других Startup-файлов) — сама фича маленькая
/// и цельная, разносить её по build-фазе/pipeline-фазе добавило бы косвенности без пользы.
/// </summary>
public static class HealthChecksRegistration
{
    public static WebApplicationBuilder AddFamilyHubHealthChecks(this WebApplicationBuilder builder)
    {
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
            Predicate = check => check.Tags.Contains("ready"),
        }).AllowAnonymous();
        app.MapHealthChecks("/health/llm", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("llm"),
        }).AllowAnonymous();

        return app;
    }
}
