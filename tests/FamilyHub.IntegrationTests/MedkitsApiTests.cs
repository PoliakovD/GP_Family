using System.Net;
using System.Net.Http.Json;
using FamilyHub.Modules.Medical.Medications;
using FamilyHub.Modules.Medical.Medkits;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

public class MedkitsApiTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private async Task<Guid> CreateFamilyAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync("/api/families", new { Name = $"Семья {Guid.NewGuid():N}" });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, Guid>>(JsonOpts);
        return body!["id"];
    }

    [Fact]
    public async Task CreateAndList_AsFamilyMember_SupportsSeveralMedkits()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);

        await admin.PostAsJsonAsync($"/api/families/{familyId}/medkits", new CreateMedkitRequest("Домашняя"));
        await admin.PostAsJsonAsync($"/api/families/{familyId}/medkits", new CreateMedkitRequest("Дорожная"));

        var listResponse = await admin.GetAsync($"/api/families/{familyId}/medkits");
        var list = await listResponse.Content.ReadFromJsonAsync<List<MedkitDto>>(JsonOpts);

        list.Should().HaveCount(2);
        list.Should().Contain(k => k.Name == "Домашняя");
        list.Should().Contain(k => k.Name == "Дорожная");
    }

    [Fact]
    public async Task List_AsOutsider_Returns403()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        var outsider = ClientAs(FreshTelegramId());

        var response = await outsider.GetAsync($"/api/families/{familyId}/medkits");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateAndDelete_AsOutsider_Returns403_AndUnknownId_Returns404()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        var created = await (await admin.PostAsJsonAsync($"/api/families/{familyId}/medkits",
            new CreateMedkitRequest("Аптечка"))).Content.ReadFromJsonAsync<MedkitDto>(JsonOpts);
        var outsider = ClientAs(FreshTelegramId());

        var forbiddenUpdate = await outsider.PutAsJsonAsync($"/api/medkits/{created!.Id}",
            new UpdateMedkitRequest("Хакнуто"));
        forbiddenUpdate.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var forbiddenDelete = await outsider.DeleteAsync($"/api/medkits/{created.Id}");
        forbiddenDelete.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var notFoundUpdate = await admin.PutAsJsonAsync($"/api/medkits/{Guid.NewGuid()}",
            new UpdateMedkitRequest("X"));
        notFoundUpdate.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var okUpdate = await admin.PutAsJsonAsync($"/api/medkits/{created.Id}", new UpdateMedkitRequest("Переименовано"));
        okUpdate.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var okDelete = await admin.DeleteAsync($"/api/medkits/{created.Id}");
        okDelete.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    /// <summary>Регресс-guard для MedkitService.GetByIdAsync — при чтении через
    /// FirstOrDefaultAsync+ToDto (вместо инлайн-проекции) MedicationCount оставался бы 0 для
    /// любой непустой аптечки, потому что Medications-навигация не подгружается без Include.</summary>
    [Fact]
    public async Task GetById_ReturnsMedkitWithMedicationCount_AndForbidsOutsider_AndUnknownId404()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        var created = await (await admin.PostAsJsonAsync($"/api/families/{familyId}/medkits",
            new CreateMedkitRequest("Аптечка"))).Content.ReadFromJsonAsync<MedkitDto>(JsonOpts);
        await admin.PostAsJsonAsync($"/api/medkits/{created!.Id}/medications",
            new CreateMedicationRequest("Аспирин", null, null));

        var ok = await admin.GetAsync($"/api/medkits/{created.Id}");
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await ok.Content.ReadFromJsonAsync<MedkitDto>(JsonOpts);
        dto!.Name.Should().Be("Аптечка");
        dto.FamilyId.Should().Be(familyId);
        dto.MedicationCount.Should().Be(1);

        var outsider = ClientAs(FreshTelegramId());
        var forbidden = await outsider.GetAsync($"/api/medkits/{created.Id}");
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var notFound = await admin.GetAsync($"/api/medkits/{Guid.NewGuid()}");
        notFound.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
