using Confluent.Kafka;
using FamilyHub.Infrastructure.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Health;

/// <summary>
/// Тег "ready". Messaging:Kafka:Enabled=false (dev-lite/юнит-тесты, ADR-0006) — бизнес-потребители
/// сидят на InMemory-шине, брокер не нужен и его отсутствие не должно валить readiness.
/// </summary>
public class KafkaHealthCheck(IOptions<MessagingOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var kafka = options.Value.Kafka;
        if (!kafka.Enabled)
            return Task.FromResult(HealthCheckResult.Healthy("Messaging:Kafka:Enabled=false — InMemory-режим."));

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
