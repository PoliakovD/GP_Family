using System.Net;
using System.Net.Http.Json;
using FamilyHub.Modules.Medical.Medications;
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

    [Fact]
    public async Task CreateAndList_AsFamilyMember_Succeeds()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);

        var createResponse = await admin.PostAsJsonAsync($"/api/families/{familyId}/medications",
            new CreateMedicationRequest("Аспирин", "По 1 таблетке", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(60)), 20));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<MedicationDto>(JsonOpts);

        var listResponse = await admin.GetAsync($"/api/families/{familyId}/medications");
        var list = await listResponse.Content.ReadFromJsonAsync<List<MedicationDto>>(JsonOpts);
        list.Should().ContainSingle(m => m.Id == created!.Id && m.Name == "Аспирин");
    }

    [Fact]
    public async Task List_AsOutsider_Returns403()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        var outsider = ClientAs(FreshTelegramId());

        var response = await outsider.GetAsync($"/api/families/{familyId}/medications");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateAndDelete_AsOutsider_Returns403_AndUnknownId_Returns404()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        var created = await (await admin.PostAsJsonAsync($"/api/families/{familyId}/medications",
            new CreateMedicationRequest("Йод", null, null, 1))).Content.ReadFromJsonAsync<MedicationDto>(JsonOpts);
        var outsider = ClientAs(FreshTelegramId());

        var forbiddenUpdate = await outsider.PutAsJsonAsync($"/api/medications/{created!.Id}",
            new UpdateMedicationRequest("Хакнуто", null, null, 1));
        forbiddenUpdate.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var forbiddenDelete = await outsider.DeleteAsync($"/api/medications/{created.Id}");
        forbiddenDelete.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var notFoundUpdate = await admin.PutAsJsonAsync($"/api/medications/{Guid.NewGuid()}",
            new UpdateMedicationRequest("X", null, null, 1));
        notFoundUpdate.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var okUpdate = await admin.PutAsJsonAsync($"/api/medications/{created.Id}",
            new UpdateMedicationRequest("Йод (обновлено)", null, null, 2));
        okUpdate.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var okDelete = await admin.DeleteAsync($"/api/medications/{created.Id}");
        okDelete.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
