using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Смок-тесты stats/keys-эндпоинтов (ADR-0009) через реальный Postgres (Testcontainers) — эта
/// поверхность не покрыта юнит-тестами вообще (raw SQL в AdminStatsService нельзя проверить на
/// SQLite, см. SqliteTestBase), поэтому только здесь ловятся ошибки синтаксиса/имён
/// колонок/схем в split_part-запросах и в проводке HealthCheckService/Hangfire IMonitoringApi.
/// </summary>
[Collection(AdminIntegrationCollection.Name)]
public class AdminStatsApiTests(AdminWebFactory factory)
{
    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/admin/session",
            new { user = AdminWebFactory.TestUser, password = AdminWebFactory.TestPassword }))
            .EnsureSuccessStatusCode();
        return client;
    }

    [Theory]
    [InlineData("/api/admin/stats/overview")]
    [InlineData("/api/admin/stats/storage")]
    [InlineData("/api/admin/stats/system")]
    [InlineData("/api/admin/stats/security")]
    [InlineData("/api/admin/keys")]
    [InlineData("/api/admin/keys/encryption/rotate/status")]
    public async Task StatsEndpoint_WithoutSession_Returns401(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/api/admin/stats/overview")]
    [InlineData("/api/admin/stats/storage")]
    [InlineData("/api/admin/stats/system")]
    [InlineData("/api/admin/stats/security")]
    [InlineData("/api/admin/keys")]
    [InlineData("/api/admin/keys/encryption/rotate/status")]
    public async Task StatsEndpoint_WithSession_Returns200(string path)
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RotateEncryption_WithSingleKeyRing_ReturnsNothingToRotate()
    {
        // AdminWebFactory (как и FamilyHubWebFactory) настраивает связку из ОДНОГО ключа
        // (Encryption:MasterKey, без PreviousKeys) — перешифровывать нечего по построению.
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsync("/api/admin/keys/encryption/rotate", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RotationStatus_WithNoRunEver_ReturnsEmptyStatus()
    {
        var client = await AuthenticatedClientAsync();

        var status = await client.GetFromJsonAsync<RotationStatusResponse>("/api/admin/keys/encryption/rotate/status");

        status!.RunId.Should().BeNull();
        status.Status.Should().BeNull();
    }

    private record RotationStatusResponse(Guid? RunId, string? TargetKeyId, string? Status);
}
