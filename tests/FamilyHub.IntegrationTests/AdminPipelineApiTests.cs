using System.Net;
using System.Net.Http.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    private record PipelineJobRowDto(Guid Id, string Type, string DisplayName, string Status, string? FailureReason);
    private record PipelineJobListTypedDto(List<PipelineJobRowDto> Rows, int Total);
    private record PurgeUnclassifiedResponseDto(int LabAnalyteDeleted, int MedicationDeleted, int VisitMedicationDeleted, int ExtractionDeleted, int TotalDeleted);

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/admin/session",
            new { user = AdminWebFactory.TestUser, password = AdminWebFactory.TestPassword }))
            .EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>Заводит одну Failed-задачу показателя напрямую через AppDbContext — тест бьёт по
    /// listing/delete/purge-логике самой админки, не по конвейеру, который её создаёт (тот же
    /// приём, что LabAnalyteKbRebuildJobTests). reason=null имитирует задачу, упавшую ДО появления
    /// EnrichmentFailureReason ("Unclassified" в «Требует внимания»).</summary>
    private async Task<Guid> SeedFailedLabAnalyteJobAsync(EnrichmentFailureReason? reason)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = new LabAnalyteEnrichmentJob
        {
            Id = Guid.NewGuid(),
            NormalizedName = $"тестпоказатель{Guid.NewGuid():N}",
            SpecimenKbId = Guid.NewGuid(),
            SourceDisplayName = "Тестовый показатель",
            RequestedByUserId = Guid.Empty,
            Status = EnrichmentJobStatus.Failed,
            Error = reason is null ? "старая ошибка без структурной причины" : "нет доверенных сниппетов",
            FailureReason = reason,
            CreatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
        };
        db.LabAnalyteEnrichmentJobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
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
    public async Task Prompts_SeededMigrationRows_AllSeventeenSlotsHaveActiveVersion()
    {
        var client = await AuthenticatedClientAsync();

        var slots = await client.GetFromJsonAsync<List<PromptSlotDto>>("/api/admin/pipeline/prompts");

        // 10 слотов LLM-промптов (AddPipelineConfig) + 3 шаблона поисковых запросов
        // (AddSearchQueryPrompts) + 1 фильтр легитимности/prompt injection (AddLegitimacyGuardPrompt)
        // + 1 гейт правдоподобности для ручного ввода (AddAnalytePlausibilityPrompt) + 1 короткое
        // название анализа отдельным шагом (AddAnalysisTitlePrompt, заметка 4) + 1 уточнение
        // родового названия показателя по "Оказанным услугам" (AddAnalyteSubjectPrompt) — тот же
        // механизм PipelinePrompt/PromptVersion на все шесть родов. analysis.specimen-resolve/
        // analysis.specimen-validate/analysis.extract получили версию 2
        // (UpdateSpecimenPromptsForSiteHint/AddAnalysisTitlePrompt) — не все слоты обязаны застыть
        // на версии 1 навсегда, важно только, что у каждого есть РОВНО одна активная версия.
        slots.Should().HaveCount(17);
        slots.Should().OnlyContain(s => s.ActiveVersion >= 1);
        slots.Should().Contain(s => s.Key == "analysis.specimen-resolve" && s.ActiveVersion == 2);
        slots.Should().Contain(s => s.Key == "analysis.specimen-validate" && s.ActiveVersion == 2);
        slots.Should().Contain(s => s.Key == "analysis.extract" && s.ActiveVersion == 2);
        slots.Should().Contain(s => s.Key == "analysis.title" && s.ActiveVersion == 1);
        slots.Should().Contain(s => s.Key == "analysis.subject-resolve" && s.ActiveVersion == 1);
        slots.Should().Contain(s => s.Key == "lab-analyte.summarize");
        slots.Should().Contain(s => s.Key == "analysis.search-query");
        slots.Should().Contain(s => s.Key == "medication.search-query.brave");
        slots.Should().Contain(s => s.Key == "medication.search-query.yandex");
        slots.Should().Contain(s => s.Key == "guard.legitimacy-check");
        slots.Should().Contain(s => s.Key == "analysis.analyte-plausibility");
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

    [Fact]
    public async Task DeleteJob_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.DeleteAsync($"/api/admin/pipeline/jobs/{Guid.NewGuid()}?type=lab-analyte");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>Регресс-тест на баг «вечно висит открытая задача»: закрытие карточки задачи теперь
    /// требует, чтобы удаление реально убирало строку — иначе «Удалить» из карточки выглядело бы
    /// как успех, но задача осталась бы в списке.</summary>
    [Fact]
    public async Task DeleteJob_KnownId_RemovesRowFromDb()
    {
        var client = await AuthenticatedClientAsync();
        var id = await SeedFailedLabAnalyteJobAsync(EnrichmentFailureReason.NoTrustedSnippets);

        var response = await client.DeleteAsync($"/api/admin/pipeline/jobs/{id}?type=lab-analyte");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.LabAnalyteEnrichmentJobs.AnyAsync(j => j.Id == id)).Should().BeFalse();
    }

    /// <summary>Регресс-тест на запрос «почистить неудавшиеся jobs, которые без объяснений» —
    /// задачи, упавшие ДО появления EnrichmentFailureReason (FailureReason=NULL, "Unclassified"
    /// в «Требует внимания»), удаляются одним действием; задачи с уже проставленной причиной,
    /// которые ещё можно чинить через карточку, purge не трогает.</summary>
    [Fact]
    public async Task PurgeUnclassified_DeletesOnlyJobsWithoutFailureReason_KeepsClassifiedOnes()
    {
        var client = await AuthenticatedClientAsync();
        var unclassifiedId = await SeedFailedLabAnalyteJobAsync(reason: null);
        var classifiedId = await SeedFailedLabAnalyteJobAsync(EnrichmentFailureReason.NoTrustedSnippets);

        try
        {
            var response = await client.PostAsync("/api/admin/pipeline/jobs/purge-unclassified", null);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<PurgeUnclassifiedResponseDto>();
            body!.LabAnalyteDeleted.Should().BeGreaterThanOrEqualTo(1);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.LabAnalyteEnrichmentJobs.AnyAsync(j => j.Id == unclassifiedId)).Should().BeFalse(
                "задача без структурной причины должна быть удалена");
            (await db.LabAnalyteEnrichmentJobs.AnyAsync(j => j.Id == classifiedId)).Should().BeTrue(
                "задача с уже проставленной причиной — не мусор, purge её не трогает");
        }
        finally
        {
            // Не оставляем классифицированную строку в общей БД коллекции — другие тесты
            // (Jobs_KnownType_ReturnsEmptyList_NotError) рассчитывают на пустую таблицу.
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.LabAnalyteEnrichmentJobs.Where(j => j.Id == classifiedId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Jobs_ReasonUnclassified_FiltersOnlyNullFailureReasonJobs()
    {
        var client = await AuthenticatedClientAsync();
        var unclassifiedId = await SeedFailedLabAnalyteJobAsync(reason: null);
        var classifiedId = await SeedFailedLabAnalyteJobAsync(EnrichmentFailureReason.NoTrustedSnippets);

        try
        {
            var response = await client.GetAsync("/api/admin/pipeline/jobs?type=lab-analyte&status=Failed&reason=Unclassified");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<PipelineJobListTypedDto>();

            body!.Rows.Should().Contain(r => r.Id == unclassifiedId);
            body.Rows.Should().NotContain(r => r.Id == classifiedId,
                "reason=Unclassified — синтетический фильтр на FailureReason IS NULL, не 'фильтр не задан'");
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.LabAnalyteEnrichmentJobs.Where(j => j.Id == unclassifiedId || j.Id == classifiedId).ExecuteDeleteAsync();
        }
    }
}
