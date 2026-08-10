using FamilyHub.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FamilyHub.Api.Health;

/// <summary>
/// Готовность к обслуживанию трафика (тег "ready") — раньше в проекте не было ни одного
/// health-эндпоинта: ни depends_on: service_healthy на api, ни k8s-подобных проб. Реальный запрос
/// к БД, а не просто "процесс жив" — миграции применяются на старте (Program.cs) с retry, и до их
/// завершения принимать трафик нет смысла.
/// </summary>
public class PostgresHealthCheck(AppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Postgres: CanConnectAsync вернул false.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Postgres недоступен.", ex);
        }
    }
}
