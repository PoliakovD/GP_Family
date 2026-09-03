using System.Net;
using System.Net.Http.Json;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.MedicalRecords;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// POST /api/medical-records/{id}/summary/regenerate — пересчёт "Резюме"/"Вопросы врачу" по
/// текущим показателям записи независимо от исходной автоматической суммаризации при
/// распознавании (нужен после ручной правки показателя, см. class doc
/// ExtractionQueryService.RegenerateSummaryAsync). LM Studio недоступен через какой-либо
/// Null-переключатель (в отличие от Extraction:Enabled/Enrichment:Provider) — фактический вызов
/// ILmStudioJsonClient здесь не проверяется вовсе, тем же приёмом, что и остальной набор
/// (IMedicalDocumentExtractor по умолчанию Null): только проводка эндпоинта — доступ, поиск
/// записи/показателей.
/// </summary>
public class RegenerateSummaryApiTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private async Task<Guid> CreateAnalysisAsync(HttpClient owner, DateOnly date)
    {
        var response = await owner.PostAsJsonAsync("/api/medical-records", new CreateMedicalRecordRequest(date, null, null, null));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<MedicalRecordDto>())!.Id;
    }

    private async Task<Guid> CreateIndicatorAsync(HttpClient owner, Guid recordId, Guid specimenId)
    {
        var response = await owner.PostAsJsonAsync($"/api/medical-records/{recordId}/indicators",
            new CreateIndicatorRequest("Гемоглобин", "140", "г/л", specimenId, "130", "160", null));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<IndicatorDto>())!.Id;
    }

    [Fact]
    public async Task RegenerateSummary_WithoutSession_Returns401()
    {
        var response = await Factory.CreateClient().PostAsync($"/api/medical-records/{Guid.NewGuid()}/summary/regenerate", null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RegenerateSummary_UnknownRecord_Returns404()
    {
        var owner = ClientAs(FreshTelegramId());

        var response = await owner.PostAsync($"/api/medical-records/{Guid.NewGuid()}/summary/regenerate", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RegenerateSummary_RecordWithoutIndicators_Returns404()
    {
        var owner = ClientAs(FreshTelegramId());
        var recordId = await CreateAnalysisAsync(owner, new DateOnly(2026, 1, 1));

        var response = await owner.PostAsync($"/api/medical-records/{recordId}/summary/regenerate", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "у записи ещё нет ни одного показателя — суммаризировать нечего");
    }

    [Fact]
    public async Task RegenerateSummary_NotOwner_Returns403()
    {
        var owner = ClientAs(FreshTelegramId());
        var specimenId = await SeedSpecimenAsync($"Кровь {Guid.NewGuid():N}");
        var recordId = await CreateAnalysisAsync(owner, new DateOnly(2026, 1, 1));
        await CreateIndicatorAsync(owner, recordId, specimenId);

        var stranger = ClientAs(FreshTelegramId());
        var response = await stranger.PostAsync($"/api/medical-records/{recordId}/summary/regenerate", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
