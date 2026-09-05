using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Выбор активной модели LM Studio из админки — реальный Postgres нужен по той же причине, что и
/// остальным AdminWebFactory-тестам (БД-backed конфигурация). FamilyHubWebFactory направляет
/// LmStudio:BaseUrl на заведомо закрытый порт (см. её class doc), поэтому /available-models
/// детерминированно отвечает LmStudioReachable=false, а не зависит от того, поднят ли на машине
/// реальный LM Studio.
/// </summary>
[Collection(AdminIntegrationCollection.Name)]
public class AdminLmStudioApiTests(AdminWebFactory factory)
{
    private record LmStudioModelInfo(string? ActiveModel, string FallbackModel);
    private record LmStudioAvailableModels(List<string> Models, bool LmStudioReachable);

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/admin/session",
            new { user = AdminWebFactory.TestUser, password = AdminWebFactory.TestPassword }))
            .EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task Model_WithoutSession_Returns401()
    {
        var response = await factory.CreateClient().GetAsync("/api/admin/lmstudio/model");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Model_NoRowYet_ReturnsNullActive_WithFallback()
    {
        var client = await AuthenticatedClientAsync();

        var info = await client.GetFromJsonAsync<LmStudioModelInfo>("/api/admin/lmstudio/model");

        info!.ActiveModel.Should().BeNull();
        info.FallbackModel.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task AvailableModels_LmStudioUnreachableInTestEnv_ReturnsEmptyList_NotError()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync("/api/admin/lmstudio/available-models");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<LmStudioAvailableModels>();
        body!.LmStudioReachable.Should().BeFalse();
        body.Models.Should().BeEmpty();
    }

    [Fact]
    public async Task SetModel_ThenReset_RoundTrips()
    {
        var client = await AuthenticatedClientAsync();

        var setResponse = await client.PutAsJsonAsync("/api/admin/lmstudio/model", new { modelId = "test-model-x" });
        setResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterSet = await client.GetFromJsonAsync<LmStudioModelInfo>("/api/admin/lmstudio/model");
        afterSet!.ActiveModel.Should().Be("test-model-x");

        var resetResponse = await client.PutAsJsonAsync("/api/admin/lmstudio/model", new { modelId = (string?)null });
        resetResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterReset = await client.GetFromJsonAsync<LmStudioModelInfo>("/api/admin/lmstudio/model");
        afterReset!.ActiveModel.Should().BeNull("пустой modelId — откат на фолбэк, не отдельное значение по умолчанию в БД");
    }
}
