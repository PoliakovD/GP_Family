using System.Net.Http.Json;
using FamilyHub.Api.Features.Dependents;
using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.MedicalRecords;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Реальный баг: динамика/список показателей смешивал РАЗНЫХ пациентов одной семьи (например,
/// несколько человек сдавали АЛТ) в одну строку/график, потому что LabIndicator группировался
/// только по (AnalyteKey, SpecimenKbId, OwnerUserId=загрузивший), не по идентичности пациента
/// (FamilyDependentId/TargetUserId). Здесь проверяется именно разделение по пациенту — "себя" и
/// зависимого (питомца/ребёнка) с одинаковым показателем, тот же приём создания записи "для
/// зависимого", что и в FamilyDependentsApiTests.
/// </summary>
public class IndicatorPatientScopingTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private record CreateFamilyResponseDto(Guid Id);
    private record CreateInviteResponseDto(string Code);
    private record PendingMemberDto(Guid UserId);
    private record FamilyDependentDto(Guid Id);
    private record MyIndicatorSummaryDto(
        string AnalyteKey, string DisplayName, Guid SpecimenKbId, string? SpecimenDisplayName, string ValueRaw, string? Unit,
        IndicatorFlag Flag, DateOnly LastRecordDate, Guid? FamilyDependentId, Guid? TargetUserId, string PatientName);

    private Guid? _bloodSpecimenId;

    private async Task<CreateIndicatorRequest> HemoglobinAsync(string value)
    {
        _bloodSpecimenId ??= await SeedSpecimenAsync("Кровь");
        return new("Гемоглобин", value, "г/л", _bloodSpecimenId.Value, "130", "160", null);
    }

    private async Task<MedicalRecordDto> CreateAnalysisAsync(HttpClient owner, DateOnly date, Guid? familyDependentId = null)
    {
        var response = await owner.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest(date, null, null, null, MedicalRecordKind.Analysis, familyDependentId));
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<MedicalRecordDto>())!;
    }

    private async Task<Guid> CreateDependentAsync(HttpClient owner, Guid familyId, string name)
    {
        var response = await owner.PostAsJsonAsync($"/api/families/{familyId}/dependents",
            new CreateFamilyDependentRequest(name, null, null, Gender.Male, null, false, null));
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<FamilyDependentDto>())!.Id;
    }

    private async Task<Guid> CreateFamilyAsync(HttpClient owner)
    {
        var response = await owner.PostAsJsonAsync("/api/families", new { Name = $"Семья {Guid.NewGuid():N}" });
        return (await response.Content.ReadFromJsonAsync<CreateFamilyResponseDto>())!.Id;
    }

    [Fact]
    public async Task GetMyIndicators_SelfAndDependent_SameAnalyte_ProduceTwoSeparateRows()
    {
        var owner = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(owner);
        var dependentId = await CreateDependentAsync(owner, familyId, $"Ребёнок{Guid.NewGuid():N}");

        var selfRecord = await CreateAnalysisAsync(owner, new DateOnly(2026, 1, 1));
        await owner.PostAsJsonAsync($"/api/medical-records/{selfRecord.Id}/indicators", await HemoglobinAsync("140"));

        var dependentRecord = await CreateAnalysisAsync(owner, new DateOnly(2026, 1, 2), dependentId);
        await owner.PostAsJsonAsync($"/api/medical-records/{dependentRecord.Id}/indicators", await HemoglobinAsync("110"));

        var summaries = await owner.GetFromJsonAsync<List<MyIndicatorSummaryDto>>("/api/indicators", JsonOpts);

        var hemoglobinRows = summaries!.Where(s => s.AnalyteKey == "гемоглобин").ToList();
        hemoglobinRows.Should().HaveCount(2,
            "гемоглобин «себя» и гемоглобин зависимого — РАЗНЫЕ пациенты, не должны схлопнуться в одну строку сводки");
        hemoglobinRows.Should().Contain(r => r.FamilyDependentId == null && r.ValueRaw == "140");
        hemoglobinRows.Should().Contain(r => r.FamilyDependentId == dependentId && r.ValueRaw == "110");
    }

    [Fact]
    public async Task GetHistory_FilteredByDependent_ExcludesSelfPoints_AndViceVersa()
    {
        var owner = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(owner);
        var dependentId = await CreateDependentAsync(owner, familyId, $"Ребёнок{Guid.NewGuid():N}");
        var specimenId = _bloodSpecimenId ??= await SeedSpecimenAsync("Кровь");

        var selfRecord = await CreateAnalysisAsync(owner, new DateOnly(2026, 1, 1));
        await owner.PostAsJsonAsync($"/api/medical-records/{selfRecord.Id}/indicators", await HemoglobinAsync("140"));

        var dependentRecord = await CreateAnalysisAsync(owner, new DateOnly(2026, 1, 2), dependentId);
        await owner.PostAsJsonAsync($"/api/medical-records/{dependentRecord.Id}/indicators", await HemoglobinAsync("110"));

        var selfHistory = await owner.GetFromJsonAsync<List<IndicatorHistoryPoint>>(
            $"/api/indicators/гемоглобин?specimenKbId={specimenId}", JsonOpts);
        var dependentHistory = await owner.GetFromJsonAsync<List<IndicatorHistoryPoint>>(
            $"/api/indicators/гемоглобин?specimenKbId={specimenId}&dependentId={dependentId}", JsonOpts);

        selfHistory.Should().ContainSingle(p => p.ValueRaw == "140");
        selfHistory.Should().NotContain(p => p.ValueRaw == "110", "без dependentId график должен быть только «моим», не смешанным");

        dependentHistory.Should().ContainSingle(p => p.ValueRaw == "110");
        dependentHistory.Should().NotContain(p => p.ValueRaw == "140");
    }
}
