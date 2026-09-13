using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.MedicalRecords;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>Отвечает {"valid": true, "reason": null} на любой вызов — и LegitimacyGuardService, и
/// AnalytePlausibilityGuardService читают ровно эти два поля, независимо от промпта, поэтому один
/// фейк удовлетворяет оба синхронных гейта заметки 3 (CheckManualEntryGatesAsync). Стандартный
/// FamilyHubWebFactory нарочно направляет LM Studio на закрытый порт (см. её class doc) — для
/// проверки того, что гейты, ПРОЙДЯ, реально ставят обогащение в очередь, нужен отдельный хост.</summary>
file sealed class AlwaysValidLmStudioJsonClient : ILmStudioJsonClient
{
    public Task<LmStudioJsonResult> ExtractJsonAsync(
        string systemPrompt, string userText, IReadOnlyList<(byte[] Bytes, string ContentType)> images,
        CancellationToken ct = default, bool suppressThinking = false) =>
        ExtractJsonAsync(systemPrompt, userText, ct, suppressThinking);

    public Task<LmStudioJsonResult> ExtractJsonAsync(
        string systemPrompt, string userText, CancellationToken ct = default, bool suppressThinking = false)
    {
        var payload = new Dictionary<string, JsonElement>
        {
            ["valid"] = JsonSerializer.SerializeToElement(true),
            ["reason"] = JsonSerializer.SerializeToElement((string?)null),
        };
        return Task.FromResult(new LmStudioJsonResult(true, payload, null));
    }
}

public class GuardPassingWebFactory : FamilyHubWebFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.AddScoped<ILmStudioJsonClient, AlwaysValidLmStudioJsonClient>();
        });
    }
}

[CollectionDefinition(Name)]
public class GuardPassingCollection : ICollectionFixture<GuardPassingWebFactory>
{
    public const string Name = "GuardPassingIntegration";
}

/// <summary>Заметка 3 — правка/добавление показателя и смена источника записи теперь проверяют
/// легитимность/правдоподобность СИНХРОННО, до постановки обогащения в очередь (не только внутри
/// фонового LabAnalyteEnrichmentProcessor). Здесь оба гейта нарочно проходят (см.
/// AlwaysValidLmStudioJsonClient) — проверяется путь "гейт прошёл → задача поставлена", зеркало
/// deny-by-default пути, уже покрытого EnrichmentPipelineTests/IndicatorCrudApiTests.</summary>
[Collection(GuardPassingCollection.Name)]
public class IndicatorEnrichmentGateTests(GuardPassingWebFactory factory) : IntegrationTestBase(factory)
{
    private static async Task<MedicalRecordDto> CreateAnalysisAsync(HttpClient owner, Guid? specimenKbId = null)
    {
        var response = await owner.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest(DateOnly.FromDateTime(DateTime.UtcNow), null, null, null));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var record = (await response.Content.ReadFromJsonAsync<MedicalRecordDto>())!;

        if (specimenKbId is { } id)
        {
            var specimenResponse = await owner.PutAsJsonAsync(
                $"/api/medical-records/{record.Id}/specimen", new SetRecordSpecimenRequest(id));
            specimenResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        return record;
    }

    private Guid? _bloodSpecimenId;

    private async Task<Guid> BloodSpecimenIdAsync() => _bloodSpecimenId ??= await SeedSpecimenAsync("Кровь");

    private static CreateIndicatorRequest SampleIndicator(string name = "Гемоглобин") =>
        new(name, "118", "г/л", "130", "160", null);

    [Fact]
    public async Task CreateIndicator_KbMiss_QueuesEnrichmentJob_ForResolvedSpecimen()
    {
        var owner = ClientAs(FreshTelegramId());
        var record = await CreateAnalysisAsync(owner, await BloodSpecimenIdAsync());
        var uniqueName = $"Тестовыйпоказатель{Guid.NewGuid():N}";

        var response = await owner.PostAsJsonAsync(
            $"/api/medical-records/{record.Id}/indicators", SampleIndicator(uniqueName));
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var analyteKey = FamilyHub.Infrastructure.Search.LabAnalyteNormalizer.Normalize(uniqueName);

        (await db.LabAnalyteEnrichmentJobs.AnyAsync(j => j.NormalizedName == analyteKey)).Should().BeTrue(
            "ручное добавление показателя ставит обогащение справочника в очередь при промахе KB, " +
            "как только синхронные гейты (заметка 3) пройдены");
    }

    [Fact]
    public async Task UpdateIndicator_KbMiss_QueuesEnrichmentJob_WithManualEntryOrigin()
    {
        var owner = ClientAs(FreshTelegramId());
        var record = await CreateAnalysisAsync(owner, await BloodSpecimenIdAsync());
        var created = (await (await owner.PostAsJsonAsync($"/api/medical-records/{record.Id}/indicators", SampleIndicator()))
            .Content.ReadFromJsonAsync<IndicatorDto>())!;

        var uniqueName = $"Тестовыйпоказатель{Guid.NewGuid():N}";
        var response = await owner.PutAsJsonAsync($"/api/indicators/{created.Id}",
            new UpdateIndicatorRequest(uniqueName, "140", "г/л", "130", "160", null));
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var analyteKey = FamilyHub.Infrastructure.Search.LabAnalyteNormalizer.Normalize(uniqueName);

        var job = await db.LabAnalyteEnrichmentJobs.SingleOrDefaultAsync(j => j.NormalizedName == analyteKey);
        job.Should().NotBeNull("правка показателя тоже ставит обогащение справочника в очередь при промахе KB, как и добавление");
        job!.Origin.Should().Be(EnrichmentRequestOrigin.ManualEntry,
            "правка — ручной ввод, задача должна пройти дополнительный гейт правдоподобности в процессоре");
    }

    /// <summary>§5 плана «живой конвейер» — ExtractionQueryService.GetIndicatorsAsync теперь
    /// джойнит показатели на LabAnalyteEnrichmentJobs (Pending/Running) той же записи, чтобы UI мог
    /// показать чип «уточняем норму…» вместо того, чтобы молча остаться без нормы навсегда.
    /// Показатель и джоба сеются напрямую в БД (не через POST .../indicators) — этот хост (
    /// GuardPassingWebFactory) держит настоящий Hangfire, реальный вызов поставил бы настоящую
    /// задачу, которую воркер мог успеть обработать (Null-провайдер веб-поиска обычно фейлится
    /// быстро) ДО того, как этот тест успеет прочитать статус — гонка с фоновым воркером, не с
    /// логикой самого джойна, которую и проверяет этот тест (сам факт постановки в очередь уже
    /// покрыт CreateIndicator_KbMiss_QueuesEnrichmentJob_ForResolvedSpecimen выше).</summary>
    [Fact]
    public async Task GetIndicators_KbMissWithPendingEnrichmentJob_EnrichmentPendingIsTrue()
    {
        var owner = ClientAs(FreshTelegramId());
        var bloodId = await BloodSpecimenIdAsync();
        var record = await CreateAnalysisAsync(owner, bloodId);
        var analyteKey = FamilyHub.Infrastructure.Search.LabAnalyteNormalizer.Normalize(
            $"Тестовыйпоказатель{Guid.NewGuid():N}");

        Guid indicatorId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var indicator = new LabIndicator
            {
                Id = Guid.NewGuid(),
                MedicalRecordId = record.Id,
                RecordDate = record.RecordDate,
                OwnerUserId = record.OwnerUserId,
                AnalyteKey = analyteKey,
                DisplayName = analyteKey,
                SpecimenKbId = bloodId,
                ValueRaw = "10",
                CreatedAt = DateTime.UtcNow,
            };
            db.LabIndicators.Add(indicator);
            db.LabAnalyteEnrichmentJobs.Add(new LabAnalyteEnrichmentJob
            {
                Id = Guid.NewGuid(),
                NormalizedName = analyteKey,
                SpecimenKbId = bloodId,
                SourceDisplayName = analyteKey,
                LabIndicatorId = indicator.Id,
                RequestedByUserId = record.OwnerUserId,
                Status = EnrichmentJobStatus.Pending,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            indicatorId = indicator.Id;
        }

        var indicators = await (await owner.GetAsync($"/api/medical-records/{record.Id}/indicators"))
            .Content.ReadFromJsonAsync<List<IndicatorDto>>();

        indicators!.Should().ContainSingle(i => i.Id == indicatorId)
            .Which.EnrichmentPending.Should().BeTrue(
                "показатель промахнулся по справочнику, а обогащение (LabAnalyteEnrichmentJob) ещё Pending — " +
                "GetIndicatorsAsync должен отразить это чипом enrichmentPending");
    }

    [Fact]
    public async Task SetRecordSpecimen_CascadesToAllIndicators_AndQueuesEnrichmentOnMiss()
    {
        var owner = ClientAs(FreshTelegramId());
        // Без specimenKbId — запись создаётся на дефолте Unresolved, показатель наследует его.
        var record = await CreateAnalysisAsync(owner);
        var uniqueName = $"Тестовыйпоказатель{Guid.NewGuid():N}";
        var created = (await (await owner.PostAsJsonAsync($"/api/medical-records/{record.Id}/indicators", SampleIndicator(uniqueName)))
            .Content.ReadFromJsonAsync<IndicatorDto>())!;
        created.SpecimenKbId.Should().Be(SpecimenContextIds.Unresolved);

        var bloodId = await BloodSpecimenIdAsync();
        var response = await owner.PutAsJsonAsync(
            $"/api/medical-records/{record.Id}/specimen", new SetRecordSpecimenRequest(bloodId));
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var indicators = await (await owner.GetAsync($"/api/medical-records/{record.Id}/indicators"))
            .Content.ReadFromJsonAsync<List<IndicatorDto>>();
        indicators!.Should().ContainSingle().Which.SpecimenKbId.Should().Be(bloodId,
            "источник — атрибут ВСЕЙ записи (заметка 1), смена записи каскадится на все её показатели");

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var analyteKey = FamilyHub.Infrastructure.Search.LabAnalyteNormalizer.Normalize(uniqueName);
        (await db.LabAnalyteEnrichmentJobs.AnyAsync(j => j.NormalizedName == analyteKey && j.SpecimenKbId == bloodId))
            .Should().BeTrue("промах справочника по (показатель, новый источник) после смены должен ставить обогащение в очередь");
    }
}
