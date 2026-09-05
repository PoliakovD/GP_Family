using System.Net;
using System.Net.Http.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.MedicalRecords;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>UX-редизайн: ручное добавление/правка/удаление показателя (без ожидания следующего
/// «Распознать») — синхронный HTTP-ответ не требует LM Studio, поэтому не нужна отдельная
/// WebFactory с фейковым клиентом (в отличие от EnrichmentPipelineTests): обогащение справочника
/// при промахе (см. class doc ExtractionQueryService.CreateIndicatorAsync) ставится в очередь
/// фоновой Hangfire-задачей и не блокирует ответ — сама задача при выполнении упрётся в
/// недоступный в тестовом хосте LM Studio (см. FamilyHubWebFactory) и завершится Failed на первом
/// шаге (LegitimacyGuardService), что и проверяется отдельно ниже. Пагинация/фильтры списка
/// записей проверены подробно на уровне сервиса (MedicalRecordServiceTests) — здесь только
/// сквозная проверка, что query-параметры HTTP-эндпоинта долетают до него и ответ действительно
/// постраничный.</summary>
public class IndicatorCrudApiTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private static async Task<MedicalRecordDto> CreateAnalysisAsync(HttpClient owner)
    {
        var response = await owner.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest(DateOnly.FromDateTime(DateTime.UtcNow), null, null, null));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<MedicalRecordDto>())!;
    }

    private Guid? _bloodSpecimenId;

    private async Task<CreateIndicatorRequest> SampleIndicatorAsync(string name = "Гемоглобин")
    {
        _bloodSpecimenId ??= await SeedSpecimenAsync("Кровь");
        return new(name, "118", "г/л", _bloodSpecimenId.Value, "130", "160", null);
    }

    [Fact]
    public async Task CreateIndicator_Owner_Succeeds_AndFlagIsComputed()
    {
        var owner = ClientAs(FreshTelegramId());
        var record = await CreateAnalysisAsync(owner);

        var response = await owner.PostAsJsonAsync($"/api/medical-records/{record.Id}/indicators", await SampleIndicatorAsync());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var indicator = await response.Content.ReadFromJsonAsync<IndicatorDto>();
        indicator!.DisplayName.Should().Be("Гемоглобин");
        indicator.Flag.Should().Be(IndicatorFlag.Low, "118 меньше нижней границы 130");
        indicator.RefSource.Should().Be(RefSource.Blank);
    }

    [Fact]
    public async Task CreateIndicator_KbMiss_QueuesEnrichmentJob_ForResolvedSpecimen()
    {
        var owner = ClientAs(FreshTelegramId());
        var record = await CreateAnalysisAsync(owner);
        var uniqueName = $"Тестовыйпоказатель{Guid.NewGuid():N}";

        var response = await owner.PostAsJsonAsync(
            $"/api/medical-records/{record.Id}/indicators", await SampleIndicatorAsync(uniqueName));
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var analyteKey = FamilyHub.Infrastructure.Search.LabAnalyteNormalizer.Normalize(uniqueName);

        (await db.LabAnalyteEnrichmentJobs.AnyAsync(j => j.NormalizedName == analyteKey)).Should().BeTrue(
            "ручное добавление показателя теперь ставит обогащение справочника в очередь при промахе KB, " +
            "тем же путём, что и распознавание документа");
    }

    [Fact]
    public async Task CreateIndicator_KbMiss_UnresolvedSpecimen_DoesNotQueueEnrichmentJob()
    {
        var owner = ClientAs(FreshTelegramId());
        var record = await CreateAnalysisAsync(owner);
        var uniqueName = $"Тестовыйпоказатель{Guid.NewGuid():N}";

        var response = await owner.PostAsJsonAsync(
            $"/api/medical-records/{record.Id}/indicators",
            new CreateIndicatorRequest(uniqueName, "118", "г/л", SpecimenContextIds.Unresolved, "130", "160", null));
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var analyteKey = FamilyHub.Infrastructure.Search.LabAnalyteNormalizer.Normalize(uniqueName);

        (await db.LabAnalyteEnrichmentJobs.AnyAsync(j => j.NormalizedName == analyteKey)).Should().BeFalse(
            "жёсткое требование — источник не определён, во внешний поиск/справочник ничего не уходит " +
            "(гейт в LabAnalyteEnrichmentRequestService)");
    }

    [Fact]
    public async Task CreateIndicator_DuplicateAnalyteAndSpecimen_ReturnsConflict()
    {
        var owner = ClientAs(FreshTelegramId());
        var record = await CreateAnalysisAsync(owner);
        (await owner.PostAsJsonAsync($"/api/medical-records/{record.Id}/indicators", await SampleIndicatorAsync()))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await owner.PostAsJsonAsync($"/api/medical-records/{record.Id}/indicators", await SampleIndicatorAsync());

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateIndicator_NotOwner_ReturnsForbidden()
    {
        var owner = ClientAs(FreshTelegramId());
        var record = await CreateAnalysisAsync(owner);
        var stranger = ClientAs(FreshTelegramId());

        var response = await stranger.PostAsJsonAsync($"/api/medical-records/{record.Id}/indicators", await SampleIndicatorAsync());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateIndicator_Owner_Succeeds_StrangerForbidden()
    {
        var owner = ClientAs(FreshTelegramId());
        var record = await CreateAnalysisAsync(owner);
        var created = (await (await owner.PostAsJsonAsync($"/api/medical-records/{record.Id}/indicators", await SampleIndicatorAsync()))
            .Content.ReadFromJsonAsync<IndicatorDto>())!;
        var stranger = ClientAs(FreshTelegramId());

        var forbidden = await stranger.PutAsJsonAsync($"/api/indicators/{created.Id}", await SampleIndicatorAsync("Гемоглобин исправленный") with { ValueRaw = "140" });
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var ownUpdate = await owner.PutAsJsonAsync($"/api/indicators/{created.Id}", await SampleIndicatorAsync("Гемоглобин исправленный") with { ValueRaw = "140" });
        ownUpdate.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var indicators = await (await owner.GetAsync($"/api/medical-records/{record.Id}/indicators"))
            .Content.ReadFromJsonAsync<List<IndicatorDto>>();
        var updated = indicators!.Should().ContainSingle().Which;
        updated.DisplayName.Should().Be("Гемоглобин исправленный");
        updated.Flag.Should().Be(IndicatorFlag.Normal, "140 внутри 130-160");
    }

    [Fact]
    public async Task DeleteIndicator_Owner_Succeeds_RemovesFromList()
    {
        var owner = ClientAs(FreshTelegramId());
        var record = await CreateAnalysisAsync(owner);
        var created = (await (await owner.PostAsJsonAsync($"/api/medical-records/{record.Id}/indicators", await SampleIndicatorAsync()))
            .Content.ReadFromJsonAsync<IndicatorDto>())!;

        var response = await owner.DeleteAsync($"/api/indicators/{created.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var indicators = await (await owner.GetAsync($"/api/medical-records/{record.Id}/indicators"))
            .Content.ReadFromJsonAsync<List<IndicatorDto>>();
        indicators.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteIndicator_NotOwner_ReturnsForbidden_UnknownId_ReturnsNotFound()
    {
        var owner = ClientAs(FreshTelegramId());
        var record = await CreateAnalysisAsync(owner);
        var created = (await (await owner.PostAsJsonAsync($"/api/medical-records/{record.Id}/indicators", await SampleIndicatorAsync()))
            .Content.ReadFromJsonAsync<IndicatorDto>())!;
        var stranger = ClientAs(FreshTelegramId());

        (await stranger.DeleteAsync($"/api/indicators/{created.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await owner.DeleteAsync($"/api/indicators/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetMedicalRecords_PageSizeAndDateFilter_ReturnExpectedSlice()
    {
        var owner = ClientAs(FreshTelegramId());
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ownerUserId = (await owner.GetFromJsonAsync<MeDto>("/api/auth/me", JsonOpts))!.UserId;
            for (var i = 0; i < 5; i++)
            {
                db.MedicalRecords.Add(new MedicalRecord
                {
                    Id = Guid.NewGuid(), OwnerUserId = ownerUserId, Kind = MedicalRecordKind.Analysis,
                    RecordDate = new DateOnly(2024, 1, 1).AddMonths(i), ExtractionStatus = ExtractionStatus.None,
                    CreatedAt = DateTime.UtcNow,
                });
            }
            await db.SaveChangesAsync();
        }

        var page1 = await owner.GetFromJsonAsync<PagedResult<MedicalRecordDto>>("/api/medical-records?pageSize=2&page=1", JsonOpts);
        var filtered = await owner.GetFromJsonAsync<PagedResult<MedicalRecordDto>>(
            "/api/medical-records?from=2024-02-01&to=2024-04-01&pageSize=100", JsonOpts);

        page1!.TotalCount.Should().Be(5);
        page1.Items.Should().HaveCount(2);
        page1.TotalPages.Should().Be(3);
        filtered!.Items.Should().HaveCount(3, "февраль/март/апрель — 3 из 5 записей попадают в диапазон");
    }

    private record MeDto(Guid UserId);
}
