using System.Net;
using System.Net.Http.Json;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.MedicalRecords;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

public class MedicalRecordsApiTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private record CreateFamilyResponseDto(Guid Id);
    private record CreateInviteResponseDto(Guid Id, string Code);
    private record PendingMemberDto(Guid UserId);

    /// <summary>Семья с админом + один обычный Active-член (через ссылочный инвайт + approve).</summary>
    private async Task<(Guid FamilyId, HttpClient Admin, HttpClient Member)> CreateFamilyWithActiveMemberAsync()
    {
        var admin = ClientAs(FreshTelegramId());
        var family = await (await admin.PostAsJsonAsync("/api/families", new { Name = $"Семья {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<CreateFamilyResponseDto>(JsonOpts);

        var invite = await (await admin.PostAsJsonAsync($"/api/families/{family!.Id}/invites",
                new CreateInviteRequest(TargetUserId: null, AssignedRole: FamilyRole.Member, MaxUses: 1, ExpiresAt: null)))
            .Content.ReadFromJsonAsync<CreateInviteResponseDto>(JsonOpts);

        var member = ClientAs(FreshTelegramId());
        await member.PostAsync($"/api/invites/{invite!.Code}/redeem", null);
        var pending = await (await admin.GetAsync($"/api/families/{family.Id}/pending"))
            .Content.ReadFromJsonAsync<List<PendingMemberDto>>(JsonOpts);
        await admin.PostAsync($"/api/families/{family.Id}/members/{pending!.Single().UserId}/approve", null);

        return (family.Id, admin, member);
    }

    private static async Task<MedicalRecordDto> CreateRecordAsync(HttpClient owner, string personName = "Иван")
    {
        var response = await owner.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest(personName, DateOnly.FromDateTime(DateTime.UtcNow), "Доктор", "Описание", null));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<MedicalRecordDto>())!;
    }

    [Fact]
    public async Task Owner_SeesOwnRecord_StrangerDoesNot()
    {
        var owner = ClientAs(FreshTelegramId());
        var record = await CreateRecordAsync(owner);
        var stranger = ClientAs(FreshTelegramId());

        var ownerList = await (await owner.GetAsync("/api/medical-records")).Content.ReadFromJsonAsync<List<MedicalRecordDto>>();
        ownerList.Should().ContainSingle(r => r.Id == record.Id);

        var strangerList = await (await stranger.GetAsync("/api/medical-records")).Content.ReadFromJsonAsync<List<MedicalRecordDto>>();
        strangerList.Should().NotContain(r => r.Id == record.Id);
    }

    [Fact]
    public async Task ShareThenHideThenUnhide_ControlsFamilyMemberVisibility()
    {
        var (familyId, owner, member) = await CreateFamilyWithActiveMemberAsync();
        var record = await CreateRecordAsync(owner);

        var memberListBeforeShare = await (await member.GetAsync("/api/medical-records")).Content.ReadFromJsonAsync<List<MedicalRecordDto>>();
        memberListBeforeShare.Should().NotContain(r => r.Id == record.Id);

        var shareResponse = await owner.PostAsJsonAsync("/api/medical-records/share", new ShareFamilyRequest(familyId));
        shareResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var memberListAfterShare = await (await member.GetAsync("/api/medical-records")).Content.ReadFromJsonAsync<List<MedicalRecordDto>>();
        memberListAfterShare.Should().ContainSingle(r => r.Id == record.Id);

        var hideResponse = await owner.PostAsJsonAsync($"/api/medical-records/{record.Id}/hide", new FamilyIdsRequest([familyId]));
        hideResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var memberListAfterHide = await (await member.GetAsync("/api/medical-records")).Content.ReadFromJsonAsync<List<MedicalRecordDto>>();
        memberListAfterHide.Should().NotContain(r => r.Id == record.Id);
        var ownerListAfterHide = await (await owner.GetAsync("/api/medical-records")).Content.ReadFromJsonAsync<List<MedicalRecordDto>>();
        ownerListAfterHide.Should().ContainSingle(r => r.Id == record.Id);

        var unhideResponse = await owner.PostAsJsonAsync($"/api/medical-records/{record.Id}/unhide", new FamilyIdsRequest([familyId]));
        unhideResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var memberListAfterUnhide = await (await member.GetAsync("/api/medical-records")).Content.ReadFromJsonAsync<List<MedicalRecordDto>>();
        memberListAfterUnhide.Should().ContainSingle(r => r.Id == record.Id);
    }

    [Fact]
    public async Task Share_WithFamilyYouAreNotMemberOf_Returns403()
    {
        var owner = ClientAs(FreshTelegramId());
        await CreateRecordAsync(owner);
        var otherAdmin = ClientAs(FreshTelegramId());
        var otherFamily = await (await otherAdmin.PostAsJsonAsync("/api/families", new { Name = $"Чужая {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<CreateFamilyResponseDto>(JsonOpts);

        var response = await owner.PostAsJsonAsync("/api/medical-records/share", new ShareFamilyRequest(otherFamily!.Id));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Hide_AsNonOwner_Returns403_AndUnknownRecord_Returns404()
    {
        var (familyId, owner, member) = await CreateFamilyWithActiveMemberAsync();
        var record = await CreateRecordAsync(owner);
        await owner.PostAsJsonAsync("/api/medical-records/share", new ShareFamilyRequest(familyId));

        var forbidden = await member.PostAsJsonAsync($"/api/medical-records/{record.Id}/hide", new FamilyIdsRequest([familyId]));
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var notFound = await owner.PostAsJsonAsync($"/api/medical-records/{Guid.NewGuid()}/hide", new FamilyIdsRequest([familyId]));
        notFound.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Unshare_UnknownFamily_Returns404()
    {
        var owner = ClientAs(FreshTelegramId());
        await CreateRecordAsync(owner);

        var response = await owner.PostAsJsonAsync("/api/medical-records/unshare", new ShareFamilyRequest(Guid.NewGuid()));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
