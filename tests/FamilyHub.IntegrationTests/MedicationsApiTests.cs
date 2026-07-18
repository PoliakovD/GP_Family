using System.Net;
using System.Net.Http.Json;
using FamilyHub.Modules.Medical.Medications;
using FamilyHub.Modules.Medical.Medkits;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

public class MedicationsApiTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private async Task<Guid> CreateFamilyAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync("/api/families", new { Name = $"Семья {Guid.NewGuid():N}" });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, Guid>>(JsonOpts);
        return body!["id"];
    }

    private async Task<Guid> CreateMedkitAsync(HttpClient admin, Guid familyId)
    {
        var response = await admin.PostAsJsonAsync($"/api/families/{familyId}/medkits", new CreateMedkitRequest("Аптечка"));
        var body = await response.Content.ReadFromJsonAsync<MedkitDto>(JsonOpts);
        return body!.Id;
    }

    [Fact]
    public async Task CreateAndList_AsFamilyMember_Succeeds()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        var medkitId = await CreateMedkitAsync(admin, familyId);

        var createResponse = await admin.PostAsJsonAsync($"/api/medkits/{medkitId}/medications",
            new CreateMedicationRequest("Аспирин", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(60)),
                new Dictionary<string, string> { ["instructions"] = "По 1 таблетке", ["quantity"] = "20" }));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<MedicationDto>(JsonOpts);

        var listResponse = await admin.GetAsync($"/api/medkits/{medkitId}/medications");
        var list = await listResponse.Content.ReadFromJsonAsync<List<MedicationDto>>(JsonOpts);
        list.Should().ContainSingle(m => m.Id == created!.Id && m.Name == "Аспирин");
    }

    [Fact]
    public async Task List_AsOutsider_Returns403()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        var medkitId = await CreateMedkitAsync(admin, familyId);
        var outsider = ClientAs(FreshTelegramId());

        var response = await outsider.GetAsync($"/api/medkits/{medkitId}/medications");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task List_UnknownMedkit_Returns404()
    {
        var admin = ClientAs(FreshTelegramId());

        var response = await admin.GetAsync($"/api/medkits/{Guid.NewGuid()}/medications");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateAndDelete_AsOutsider_Returns403_AndUnknownId_Returns404()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        var medkitId = await CreateMedkitAsync(admin, familyId);
        var created = await (await admin.PostAsJsonAsync($"/api/medkits/{medkitId}/medications",
            new CreateMedicationRequest("Йод", null, new Dictionary<string, string> { ["quantity"] = "1" })))
            .Content.ReadFromJsonAsync<MedicationDto>(JsonOpts);
        var outsider = ClientAs(FreshTelegramId());

        var forbiddenUpdate = await outsider.PutAsJsonAsync($"/api/medications/{created!.Id}",
            new UpdateMedicationRequest("Хакнуто", null, new Dictionary<string, string> { ["quantity"] = "1" }));
        forbiddenUpdate.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var forbiddenDelete = await outsider.DeleteAsync($"/api/medications/{created.Id}");
        forbiddenDelete.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var notFoundUpdate = await admin.PutAsJsonAsync($"/api/medications/{Guid.NewGuid()}",
            new UpdateMedicationRequest("X", null, new Dictionary<string, string> { ["quantity"] = "1" }));
        notFoundUpdate.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var okUpdate = await admin.PutAsJsonAsync($"/api/medications/{created.Id}",
            new UpdateMedicationRequest("Йод (обновлено)", null, new Dictionary<string, string> { ["quantity"] = "2" }));
        okUpdate.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var okDelete = await admin.DeleteAsync($"/api/medications/{created.Id}");
        okDelete.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Family_CanHaveSeveralMedkits_WithIndependentMedications()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        var medkitA = await CreateMedkitAsync(admin, familyId);
        var medkitB = await CreateMedkitAsync(admin, familyId);

        await admin.PostAsJsonAsync($"/api/medkits/{medkitA}/medications",
            new CreateMedicationRequest("Аспирин", null, new Dictionary<string, string> { ["quantity"] = "5" }));
        await admin.PostAsJsonAsync($"/api/medkits/{medkitB}/medications",
            new CreateMedicationRequest("Йод", null, new Dictionary<string, string> { ["quantity"] = "1" }));

        var listA = await (await admin.GetAsync($"/api/medkits/{medkitA}/medications"))
            .Content.ReadFromJsonAsync<List<MedicationDto>>(JsonOpts);
        var listB = await (await admin.GetAsync($"/api/medkits/{medkitB}/medications"))
            .Content.ReadFromJsonAsync<List<MedicationDto>>(JsonOpts);

        listA.Should().ContainSingle(m => m.Name == "Аспирин");
        listB.Should().ContainSingle(m => m.Name == "Йод");
    }
}
