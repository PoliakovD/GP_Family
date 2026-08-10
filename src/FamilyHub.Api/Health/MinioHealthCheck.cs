using FamilyHub.Infrastructure.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace FamilyHub.Api.Health;

/// <summary>Тег "ready" — вложения (медицинские сканы) хранятся только в MinIO (LocalFileStorage упразднён).</summary>
public class MinioHealthCheck(IMinioClient client, IOptions<MinioOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var bucket = options.Value.Bucket;
        try
        {
            var exists = await client.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket), cancellationToken);
            return exists
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"MinIO: бакет '{bucket}' не найден.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("MinIO недоступен.", ex);
        }
    }
}
