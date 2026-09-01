using System.Net;
using System.Net.Http.Json;
using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.MedicalRecords;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>GET /api/medical-records/{recordId} — редизайн v3 (мобильный экран открытой записи,
/// PR6): в отличие от UpdateAsync/DeleteAsync (только владелец), это чтение по VisibleRecordsQuery
/// — доступно всем, кому запись видна (расшарена/назначена/подопечный семьи), не только владельцу.
/// NotFound vs Forbidden должны различаться (не существует vs существует, но не видна).</summary>
public class MedicalRecordGetByIdTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private record MeDto(Guid UserId);

    private static async Task<MedicalRecordDto> CreateAnalysisAsync(HttpClient owner, DateOnly date)
    {
        var response = await owner.PostAsJsonAsync("/api/medical-records", new CreateMedicalRecordRequest(date, null, null, null));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<MedicalRecordDto>())!;
    }

    private async Task<Guid> CreateFamilyAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/families", new { Name = $"Семья {Guid.NewGuid():N}" });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, Guid>>(JsonOpts);
        return body!["id"];
    }

    [Fact]
    public async Task GetById_Owner_ReturnsRecord_WithHiddenFamilyIds()
    {
        var owner = ClientAs(FreshTelegramId());
        var record = await CreateAnalysisAsync(owner, new DateOnly(2026, 1, 1));

        var fetched = await owner.GetFromJsonAsync<MedicalRecordDto>($"/api/medical-records/{record.Id}", JsonOpts);

        fetched!.Id.Should().Be(record.Id);
    }

    [Fact]
    public async Task GetById_UnknownId_ReturnsNotFound()
    {
        var owner = ClientAs(FreshTelegramId());

        var response = await owner.GetAsync($"/api/medical-records/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetById_StrangerWithNoAccess_ReturnsForbidden_NotNotFound()
    {
        var owner = ClientAs(FreshTelegramId());
        var stranger = ClientAs(FreshTelegramId());
        var record = await CreateAnalysisAsync(owner, DateOnly.FromDateTime(DateTime.UtcNow));

        var response = await stranger.GetAsync($"/api/medical-records/{record.Id}");

        // Forbidden, не NotFound — запись существует, просто не видна этому пользователю
        // (тот же приём различения, что ExtractionQueryService.CheckAccessAsync).
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetById_ViewerWithFamilyShare_CanReadRecord_ButNotHiddenFamilyIds()
    {
        var owner = ClientAs(FreshTelegramId());
        var viewer = ClientAs(FreshTelegramId());

        var familyId = await CreateFamilyAsync(owner);
        var invite = await (await owner.PostAsJsonAsync($"/api/families/{familyId}/invites",
                new { TargetUserId = (Guid?)null, AssignedRole = FamilyRole.Member, MaxUses = 5, ExpiresAt = (DateTime?)null }))
            .Content.ReadFromJsonAsync<Dictionary<string, object>>(JsonOpts);
        var code = invite!["code"].ToString();
        await viewer.PostAsync($"/api/invites/{code}/redeem", null);
        var viewerUserId = (await viewer.GetFromJsonAsync<MeDto>("/api/auth/me", JsonOpts))!.UserId;
        (await owner.PostAsync($"/api/families/{familyId}/members/{viewerUserId}/approve", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var record = await CreateAnalysisAsync(owner, new DateOnly(2026, 2, 1));
        (await owner.PostAsJsonAsync("/api/medical-records/share", new { FamilyId = familyId }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var viewerCopy = await viewer.GetFromJsonAsync<MedicalRecordDto>($"/api/medical-records/{record.Id}", JsonOpts);
        viewerCopy!.Id.Should().Be(record.Id);
        // HiddenFamilyIds — только владельцу (личная настройка доступа), не тем, кому расшарено.
        viewerCopy.HiddenFamilyIds.Should().BeEmpty();
    }

    [Fact]
    public async Task GetById_ViewerAfterHide_ReturnsForbidden()
    {
        var owner = ClientAs(FreshTelegramId());
        var viewer = ClientAs(FreshTelegramId());

        var familyId = await CreateFamilyAsync(owner);
        var invite = await (await owner.PostAsJsonAsync($"/api/families/{familyId}/invites",
                new { TargetUserId = (Guid?)null, AssignedRole = FamilyRole.Member, MaxUses = 5, ExpiresAt = (DateTime?)null }))
            .Content.ReadFromJsonAsync<Dictionary<string, object>>(JsonOpts);
        var code = invite!["code"].ToString();
        await viewer.PostAsync($"/api/invites/{code}/redeem", null);
        var viewerUserId = (await viewer.GetFromJsonAsync<MeDto>("/api/auth/me", JsonOpts))!.UserId;
        (await owner.PostAsync($"/api/families/{familyId}/members/{viewerUserId}/approve", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var record = await CreateAnalysisAsync(owner, new DateOnly(2026, 3, 1));
        (await owner.PostAsJsonAsync("/api/medical-records/share", new { FamilyId = familyId }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.PostAsJsonAsync($"/api/medical-records/{record.Id}/hide", new { FamilyIds = new[] { familyId } }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await viewer.GetAsync($"/api/medical-records/{record.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
