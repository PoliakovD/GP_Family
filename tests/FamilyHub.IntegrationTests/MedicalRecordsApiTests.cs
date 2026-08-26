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

    /// <summary>UX-редизайн: GET /api/medical-records теперь пагинирован
    /// (PagedResult&lt;MedicalRecordDto&gt;) — обёртка возвращает голый список, как раньше
    /// делали тесты напрямую; PageSize=100 достаточен для всех сценариев этого файла.</summary>
    private static async Task<List<MedicalRecordDto>> GetRecordsAsync(HttpClient client, string query = "")
    {
        var separator = query.Length > 0 ? "&" : "?";
        var response = await client.GetAsync($"/api/medical-records{query}{separator}pageSize=100");
        var page = await response.Content.ReadFromJsonAsync<PagedResult<MedicalRecordDto>>();
        return [.. page!.Items];
    }

    private static async Task<MedicalRecordDto> CreateRecordAsync(HttpClient owner)
    {
        var response = await owner.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest(DateOnly.FromDateTime(DateTime.UtcNow), "Доктор", "Описание", null));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<MedicalRecordDto>())!;
    }

    [Fact]
    public async Task Owner_SeesOwnRecord_StrangerDoesNot()
    {
        var owner = ClientAs(FreshTelegramId());
        var record = await CreateRecordAsync(owner);
        var stranger = ClientAs(FreshTelegramId());

        var ownerList = await GetRecordsAsync(owner);
        ownerList.Should().ContainSingle(r => r.Id == record.Id);

        var strangerList = await GetRecordsAsync(stranger);
        strangerList.Should().NotContain(r => r.Id == record.Id);
    }

    [Fact]
    public async Task ShareThenHideThenUnhide_ControlsFamilyMemberVisibility()
    {
        var (familyId, owner, member) = await CreateFamilyWithActiveMemberAsync();
        var record = await CreateRecordAsync(owner);

        var memberListBeforeShare = await GetRecordsAsync(member);
        memberListBeforeShare.Should().NotContain(r => r.Id == record.Id);

        var shareResponse = await owner.PostAsJsonAsync("/api/medical-records/share", new ShareFamilyRequest(familyId));
        shareResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var memberListAfterShare = await GetRecordsAsync(member);
        memberListAfterShare.Should().ContainSingle(r => r.Id == record.Id);

        var hideResponse = await owner.PostAsJsonAsync($"/api/medical-records/{record.Id}/hide", new FamilyIdsRequest([familyId]));
        hideResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var memberListAfterHide = await GetRecordsAsync(member);
        memberListAfterHide.Should().NotContain(r => r.Id == record.Id);
        var ownerListAfterHide = await GetRecordsAsync(owner);
        ownerListAfterHide.Should().ContainSingle(r => r.Id == record.Id);

        var unhideResponse = await owner.PostAsJsonAsync($"/api/medical-records/{record.Id}/unhide", new FamilyIdsRequest([familyId]));
        unhideResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var memberListAfterUnhide = await GetRecordsAsync(member);
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

    [Fact]
    public async Task Create_WithoutKind_DefaultsToAnalysis()
    {
        // Позиционный вызов без Kind (как у старых клиентов) — обратная совместимость.
        var owner = ClientAs(FreshTelegramId());

        var record = await CreateRecordAsync(owner);

        record.Kind.Should().Be(MedicalRecordKind.Analysis);
    }

    [Fact]
    public async Task Create_DoctorVisit_And_KindQueryParam_FiltersList()
    {
        var owner = ClientAs(FreshTelegramId());
        var analysis = await CreateRecordAsync(owner);
        var visitResponse = await owner.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest(
                DateOnly.FromDateTime(DateTime.UtcNow), "Кардиолог", null, null, MedicalRecordKind.DoctorVisit));
        visitResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var visit = (await visitResponse.Content.ReadFromJsonAsync<MedicalRecordDto>())!;
        visit.Kind.Should().Be(MedicalRecordKind.DoctorVisit);

        var all = await GetRecordsAsync(owner);
        all.Should().Contain(r => r.Id == analysis.Id).And.Contain(r => r.Id == visit.Id);

        var analysesOnly = await GetRecordsAsync(owner, "?kind=analysis");
        analysesOnly.Should().ContainSingle(r => r.Id == analysis.Id);
        analysesOnly.Should().NotContain(r => r.Id == visit.Id);

        var visitsOnly = await GetRecordsAsync(owner, "?kind=visit");
        visitsOnly.Should().ContainSingle(r => r.Id == visit.Id);
        visitsOnly.Should().NotContain(r => r.Id == analysis.Id);
    }

    [Fact]
    public async Task Create_ForTargetUserInSameFamily_Succeeds_AndVisibleToTarget_OwnerStaysUploader()
    {
        var (_, owner, target) = await CreateFamilyWithActiveMemberAsync();
        var targetUserId = await GetMyUserIdAsync(target);

        var record = await CreateForTargetAsync(owner, targetUserId);

        record.TargetUserId.Should().Be(targetUserId);
        record.OwnerUserId.Should().NotBe(targetUserId, "владелец — тот, кто физически загрузил, а не получатель");
        (await GetRecordsAsync(target)).Should().ContainSingle(r => r.Id == record.Id);
    }

    [Fact]
    public async Task Create_ForTargetUserWithoutSharedFamily_ReturnsForbidden()
    {
        var owner = ClientAs(FreshTelegramId());
        var stranger = ClientAs(FreshTelegramId());
        var strangerUserId = await GetMyUserIdAsync(stranger);

        var response = await owner.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest(
                DateOnly.FromDateTime(DateTime.UtcNow), null, null, null, TargetUserId: strangerUserId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_BothDependentAndTargetSet_ReturnsBadRequest()
    {
        var (_, owner, target) = await CreateFamilyWithActiveMemberAsync();
        var targetUserId = await GetMyUserIdAsync(target);

        var response = await owner.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest(
                DateOnly.FromDateTime(DateTime.UtcNow), null, null, null,
                FamilyDependentId: Guid.NewGuid(), TargetUserId: targetUserId));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_Owner_Succeeds_TargetCannotDelete_UnconditionalOwnerOnlyRule()
    {
        var (_, owner, target) = await CreateFamilyWithActiveMemberAsync();
        var targetUserId = await GetMyUserIdAsync(target);
        var record = await CreateForTargetAsync(owner, targetUserId);

        var targetAttempt = await target.DeleteAsync($"/api/medical-records/{record.Id}");
        targetAttempt.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var ownerAttempt = await owner.DeleteAsync($"/api/medical-records/{record.Id}");
        ownerAttempt.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await target.GetAsync("/api/medical-records")).StatusCode.Should().Be(HttpStatusCode.OK);
        var targetListAfter = await GetRecordsAsync(target);
        targetListAfter.Should().NotContain(r => r.Id == record.Id);
    }

    [Fact]
    public async Task UpdateRecord_Owner_ChangesDateDoctorDescription()
    {
        var owner = ClientAs(FreshTelegramId());
        var record = await CreateRecordAsync(owner);
        var newDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3);

        var response = await owner.PutAsJsonAsync($"/api/medical-records/{record.Id}",
            new UpdateMedicalRecordRequest(newDate, "Новый врач", "Новое описание"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<MedicalRecordDto>();
        updated!.RecordDate.Should().Be(newDate);
        updated.Doctor.Should().Be("Новый врач");
        updated.Description.Should().Be("Новое описание");
    }

    [Fact]
    public async Task UpdateRecord_NotOwner_ReturnsForbidden_UnknownRecord_ReturnsNotFound()
    {
        var owner = ClientAs(FreshTelegramId());
        var record = await CreateRecordAsync(owner);
        var stranger = ClientAs(FreshTelegramId());
        var patch = new UpdateMedicalRecordRequest(record.RecordDate, "X", null);

        (await stranger.PutAsJsonAsync($"/api/medical-records/{record.Id}", patch)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await owner.PutAsJsonAsync($"/api/medical-records/{Guid.NewGuid()}", patch)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_UnknownRecord_ReturnsNotFound()
    {
        var owner = ClientAs(FreshTelegramId());

        var response = await owner.DeleteAsync($"/api/medical-records/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private record MeDto(Guid UserId);

    private static async Task<Guid> GetMyUserIdAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/auth/me")).Content.ReadFromJsonAsync<MeDto>(JsonOpts)).UserId;

    private static async Task<MedicalRecordDto> CreateForTargetAsync(HttpClient owner, Guid targetUserId)
    {
        var response = await owner.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest(
                DateOnly.FromDateTime(DateTime.UtcNow), null, null, null, TargetUserId: targetUserId));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<MedicalRecordDto>(JsonOpts))!;
    }

    [Fact]
    public async Task GetAttachments_OwnerSeesThem_OutsiderForbidden_UnknownRecordNotFound()
    {
        var (familyId, owner, member) = await CreateFamilyWithActiveMemberAsync();
        var record = await CreateRecordAsync(owner);
        var stranger = ClientAs(FreshTelegramId());

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("scan-bytes"));
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", "scan.pdf");
        (await owner.PostAsync($"/api/medical-records/{record.Id}/attachments", content)).StatusCode.Should().Be(HttpStatusCode.Created);

        var ownerList = await owner.GetAsync($"/api/medical-records/{record.Id}/attachments");
        ownerList.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ownerList.Content.ReadFromJsonAsync<List<object>>())!.Should().ContainSingle();

        var strangerList = await stranger.GetAsync($"/api/medical-records/{record.Id}/attachments");
        strangerList.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Расшарить семье — участник тоже должен увидеть список вложений.
        await owner.PostAsJsonAsync("/api/medical-records/share", new ShareFamilyRequest(familyId));
        var memberList = await member.GetAsync($"/api/medical-records/{record.Id}/attachments");
        memberList.StatusCode.Should().Be(HttpStatusCode.OK);

        var notFound = await owner.GetAsync($"/api/medical-records/{Guid.NewGuid()}/attachments");
        notFound.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
