using Confluent.Kafka;
using FamilyHub.TelegramBot.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace FamilyHub.TelegramBot.Health;

/// <summary>
/// Тег "ready" — копия FamilyHub.Api.Health.KafkaHealthCheck под BotMessagingOptions бота.
/// Messaging:Kafka:Enabled=false (локальная разработка без брокера) — брокер не нужен и его
/// отсутствие не должно валить readiness (бот тогда обслуживает только вебхук).
/// </summary>
public class KafkaHealthCheck(IOptions<BotMessagingOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var kafka = options.Value.Kafka;
        if (!kafka.Enabled)
            return Task.FromResult(HealthCheckResult.Healthy("Messaging:Kafka:Enabled=false — брокер не используется."));

        try
        {
            using var admin = new AdminClientBuilder(new AdminClientConfig
            {
                BootstrapServers = kafka.BootstrapServers,
            }).Build();
            var metadata = admin.GetMetadata(TimeSpan.FromSeconds(5));
            return Task.FromResult(metadata.Brokers.Count > 0
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Kafka: метаданные не содержат ни одного брокера."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Kafka недоступен.", ex));
        }
    }
}
