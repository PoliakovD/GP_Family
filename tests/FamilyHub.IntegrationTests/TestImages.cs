namespace FamilyHub.IntegrationTests;

/// <summary>Образы для Testcontainers, которые не берутся из умолчаний библиотеки.</summary>
public static class TestImages
{
    /// <summary>
    /// MinIO собственной сборки (ADR-0012, workflow .github/workflows/minio-image.yml) — тот же образ,
    /// что в docker-compose.yml и docker-compose.prod.yml: тесты идут против ровно той версии, которая
    /// работает на проде. Умолчание Testcontainers.Minio (minio/minio:RELEASE.2023-01-31...) — образ
    /// проекта, который больше не публикуется. Тег обновлять вместе с ARG MINIO_REF в deploy/minio/Dockerfile.
    /// </summary>
    public const string Minio = "ghcr.io/poliakovd/gp_family-minio:RELEASE.2025-10-15T17-29-55Z";
}
