using System.Net;
using System.Net.Http.Json;
using FamilyHub.Modules.Birthdays.Birthdays;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

public class BirthdaysApiTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
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

        var createResponse = await admin.PostAsJsonAsync($"/api/families/{familyId}/birthdays",
            new CreateBirthdayRequest("Бабушка", new DateOnly(1950, 5, 17)));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<BirthdayDto>(JsonOpts);

        var list = await (await admin.GetAsync($"/api/families/{familyId}/birthdays")).Content.ReadFromJsonAsync<List<BirthdayDto>>(JsonOpts);
        list.Should().ContainSingle(b => b.Id == created!.Id && b.PersonName == "Бабушка");
    }

    [Fact]
    public async Task List_AsOutsider_Returns403()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        var outsider = ClientAs(FreshTelegramId());

        var response = await outsider.GetAsync($"/api/families/{familyId}/birthdays");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateAndDelete_AsOutsider_Returns403_AndUnknownId_Returns404()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        var created = await (await admin.PostAsJsonAsync($"/api/families/{familyId}/birthdays",
            new CreateBirthdayRequest("Дедушка", new DateOnly(1945, 1, 1)))).Content.ReadFromJsonAsync<BirthdayDto>(JsonOpts);
        var outsider = ClientAs(FreshTelegramId());

        var forbiddenUpdate = await outsider.PutAsJsonAsync($"/api/birthdays/{created!.Id}",
            new UpdateBirthdayRequest("Хакнуто", new DateOnly(2000, 1, 1)));
        forbiddenUpdate.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var forbiddenDelete = await outsider.DeleteAsync($"/api/birthdays/{created.Id}");
        forbiddenDelete.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var notFoundUpdate = await admin.PutAsJsonAsync($"/api/birthdays/{Guid.NewGuid()}",
            new UpdateBirthdayRequest("X", new DateOnly(2000, 1, 1)));
        notFoundUpdate.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var okUpdate = await admin.PutAsJsonAsync($"/api/birthdays/{created.Id}",
            new UpdateBirthdayRequest("Дедушка (обновлено)", new DateOnly(1945, 2, 2)));
        okUpdate.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var okDelete = await admin.DeleteAsync($"/api/birthdays/{created.Id}");
        okDelete.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
