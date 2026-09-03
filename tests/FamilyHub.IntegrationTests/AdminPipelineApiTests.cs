using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Управление enrich-пайплайном из админки (§2 плана) — вкл/выкл необязательных шагов,
/// версионирование промптов, листинг задач конвейеров, всё через реальный Postgres
/// (Testcontainers), т.к. конфигурация теперь БД-backed.
/// </summary>
[Collection(AdminIntegrationCollection.Name)]
public class AdminPipelineApiTests(AdminWebFactory factory)
{
    private record PipelineStepDto(string PipelineKey, string StepKey, string Description, bool IsMandatory, bool IsEnabled, string? PromptKey);
    private record PromptSlotDto(string Key, string Description, int? ActiveVersion, DateTime? ActiveVersionCreatedAt);
    private record PromptVersionDto(Guid Id, int Version, bool IsActive, string? Note, DateTime CreatedAt, string Body);
    private record DryRunResponseDto(bool Success, string? Error, Dictionary<string, object>? Payload);
    private record PipelineJobListDto(List<object> Rows, int Total);

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/admin/session",
            new { user = AdminWebFactory.TestUser, password = AdminWebFactory.TestPassword }))
            .EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task Pipelines_WithoutSession_Returns401()
    {
        var response = await factory.CreateClient().GetAsync("/api/admin/pipeline/pipelines");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Pipelines_ListsSeededCatalogSteps_WithMandatoryFlags()
    {
        var client = await AuthenticatedClientAsync();

        var steps = await client.GetFromJsonAsync<List<PipelineStepDto>>("/api/admin/pipeline/pipelines");

        steps.Should().Contain(s => s.PipelineKey == "analysis-extraction" && s.StepKey == "extract" && s.IsMandatory && s.IsEnabled);
        steps.Should().Contain(s => s.PipelineKey == "analysis-extraction" && s.StepKey == "ocr-correct" && !s.IsMandatory && s.IsEnabled);
    }

    [Fact]
    public async Task ToggleStep_MandatoryStep_Returns409_NeverDisabled()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PutAsJsonAsync(
            "/api/admin/pipeline/pipelines/analysis-extraction/steps/extract", new { isEnabled = false });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ToggleStep_OptionalStep_UnknownStepKey_Returns404()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PutAsJsonAsync(
            $"/api/admin/pipeline/pipelines/analysis-extraction/steps/never-existed-{Guid.NewGuid():N}", new { isEnabled = false });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ToggleStep_OptionalStep_DisableThenEnable_Persists()
    {
        var client = await AuthenticatedClientAsync();

        var disable = await client.PutAsJsonAsync(
            "/api/admin/pipeline/pipelines/analysis-extraction/steps/record-summary", new { isEnabled = false });
        disable.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterDisable = await client.GetFromJsonAsync<List<PipelineStepDto>>("/api/admin/pipeline/pipelines");
        afterDisable.Should().Contain(s => s.StepKey == "record-summary" && !s.IsEnabled);

        var enable = await client.PutAsJsonAsync(
            "/api/admin/pipeline/pipelines/analysis-extraction/steps/record-summary", new { isEnabled = true });
        enable.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterEnable = await client.GetFromJsonAsync<List<PipelineStepDto>>("/api/admin/pipeline/pipelines");
        afterEnable.Should().Contain(s => s.StepKey == "record-summary" && s.IsEnabled);
    }

    [Fact]
    public async Task Prompts_SeededMigrationRows_AllTenSlotsHaveActiveVersion1()
    {
        var client = await AuthenticatedClientAsync();

        var slots = await client.GetFromJsonAsync<List<PromptSlotDto>>("/api/admin/pipeline/prompts");

        slots.Should().HaveCount(10);
        slots.Should().OnlyContain(s => s.ActiveVersion == 1);
        slots.Should().Contain(s => s.Key == "analysis.extract");
        slots.Should().Contain(s => s.Key == "lab-analyte.summarize");
    }

    [Fact]
    public async Task PromptVersions_UnknownKey_Returns404()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/admin/pipeline/prompts/never-existed-{Guid.NewGuid():N}/versions");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreatePromptVersion_ThenActivateOlder_FullRollbackLifecycle()
    {
        var client = await AuthenticatedClientAsync();

        var v1Versions = await client.GetFromJsonAsync<List<PromptVersionDto>>("/api/admin/pipeline/prompts/analysis.ocr-correct/versions");
        v1Versions.Should().ContainSingle(v => v.IsActive && v.Version == 1);

        var createResponse = await client.PostAsJsonAsync(
            "/api/admin/pipeline/prompts/analysis.ocr-correct/versions",
            new { body = "новый текст промпта для теста", note = "integration test" });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var v2 = await createResponse.Content.ReadFromJsonAsync<PromptVersionDto>();
        v2!.Version.Should().Be(2);
        v2.IsActive.Should().BeTrue();

        var afterCreate = await client.GetFromJsonAsync<List<PromptSlotDto>>("/api/admin/pipeline/prompts");
        afterCreate.Should().Contain(s => s.Key == "analysis.ocr-correct" && s.ActiveVersion == 2);

        // Откат — активация версии 1 обратно, ничего не удаляется (обе версии остаются в истории).
        var activateResponse = await client.PostAsync("/api/admin/pipeline/prompts/analysis.ocr-correct/activate/1", null);
        activateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterRollback = await client.GetFromJsonAsync<List<PromptSlotDto>>("/api/admin/pipeline/prompts");
        afterRollback.Should().Contain(s => s.Key == "analysis.ocr-correct" && s.ActiveVersion == 1);

        var versionsAfterRollback = await client.GetFromJsonAsync<List<PromptVersionDto>>(
            "/api/admin/pipeline/prompts/analysis.ocr-correct/versions");
        versionsAfterRollback.Should().HaveCount(2, "откат не удаляет версии, только переключает IsActive");
        versionsAfterRollback.Should().ContainSingle(v => v.IsActive);
    }

    [Fact]
    public async Task ActivateVersion_UnknownVersion_Returns404()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsync("/api/admin/pipeline/prompts/analysis.extract/activate/999", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DryRun_MissingUserText_ReturnsBadRequest()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/admin/pipeline/prompts/dry-run", new { promptKey = "analysis.extract", bodyOverride = (string?)null, userText = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DryRun_LmStudioUnreachableInTestEnv_ReturnsOkWithSuccessFalse_NotAnException()
    {
        // Нулевой egress (§2.3 плана) — dry-run не ходит во внешний поиск; сам LM Studio в
        // интеграционных тестах не поднят, поэтому ожидаем аккуратный Success=false, а не 5xx.
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/admin/pipeline/prompts/dry-run",
            new { promptKey = "analysis.extract", bodyOverride = "тестовый промпт", userText = "образец текста" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DryRunResponseDto>();
        body!.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Jobs_MissingType_ReturnsBadRequest()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync("/api/admin/pipeline/jobs");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Jobs_KnownType_ReturnsEmptyList_NotError()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/admin/pipeline/jobs?type=lab-analyte&skip=0&take=25");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PipelineJobListDto>();
        body!.Total.Should().Be(0);
    }

    [Fact]
    public async Task RetryJob_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsync($"/api/admin/pipeline/jobs/{Guid.NewGuid()}/retry?type=lab-analyte", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
