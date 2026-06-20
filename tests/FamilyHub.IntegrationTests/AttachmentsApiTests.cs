using System.Net;
using System.Net.Http.Json;
using System.Text;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.Attachments;
using FamilyHub.Modules.Medical.MedicalRecords;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

public class AttachmentsApiTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private record CreateFamilyResponseDto(Guid Id);
    private record CreateInviteResponseDto(Guid Id, string Code);
    private record PendingMemberDto(Guid UserId);

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

    private static async Task<MedicalRecordDto> CreateRecordAsync(HttpClient owner)
    {
        var response = await owner.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest("Иван", DateOnly.FromDateTime(DateTime.UtcNow), "Доктор", "Описание", null));
        return (await response.Content.ReadFromJsonAsync<MedicalRecordDto>())!;
    }

    private static MultipartFormDataContent BuildUpload(string text = "scan-content")
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content.Add(fileContent, "file", "scan.txt");
        return content;
    }

    [Fact]
    public async Task Upload_AsOwner_Returns201_AndOutsider_Returns403_AndUnknownRecord_Returns404()
    {
        var owner = ClientAs(FreshTelegramId());
        var record = await CreateRecordAsync(owner);
        var outsider = ClientAs(FreshTelegramId());

        var createResponse = await owner.PostAsync($"/api/medical-records/{record.Id}/attachments", BuildUpload());
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<AttachmentDto>(JsonOpts);
        created!.FileName.Should().Be("scan.txt");

        var forbidden = await outsider.PostAsync($"/api/medical-records/{record.Id}/attachments", BuildUpload());
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var notFound = await owner.PostAsync($"/api/medical-records/{Guid.NewGuid()}/attachments", BuildUpload());
        notFound.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PresignedUrl_OwnerAndSharedMember_GetIt_OutsiderForbidden_UnknownNotFound()
    {
        var (familyId, owner, member) = await CreateFamilyWithActiveMemberAsync();
        var record = await CreateRecordAsync(owner);
        var attachment = await (await owner.PostAsync($"/api/medical-records/{record.Id}/attachments", BuildUpload()))
            .Content.ReadFromJsonAsync<AttachmentDto>(JsonOpts);
        var outsider = ClientAs(FreshTelegramId());

        var ownerUrlResponse = await owner.GetAsync($"/api/attachments/{attachment!.Id}/url");
        ownerUrlResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var memberUrlBeforeShare = await member.GetAsync($"/api/attachments/{attachment.Id}/url");
        memberUrlBeforeShare.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await owner.PostAsJsonAsync("/api/medical-records/share", new ShareFamilyRequest(familyId));
        var memberUrlAfterShare = await member.GetAsync($"/api/attachments/{attachment.Id}/url");
        memberUrlAfterShare.StatusCode.Should().Be(HttpStatusCode.OK);

        var outsiderUrl = await outsider.GetAsync($"/api/attachments/{attachment.Id}/url");
        outsiderUrl.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var unknownUrl = await owner.GetAsync($"/api/attachments/{Guid.NewGuid()}/url");
        unknownUrl.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task LocalFileLink_ServesContent_AndTamperedSignature_Returns401()
    {
        var owner = ClientAs(FreshTelegramId());
        var record = await CreateRecordAsync(owner);
        var attachment = await (await owner.PostAsync($"/api/medical-records/{record.Id}/attachments", BuildUpload("hello-from-test")))
            .Content.ReadFromJsonAsync<AttachmentDto>(JsonOpts);

        var urlBody = await (await owner.GetAsync($"/api/attachments/{attachment!.Id}/url")).Content.ReadFromJsonAsync<Dictionary<string, string>>(JsonOpts);
        var relativeUrl = urlBody!["url"];

        var anonymous = AnonymousClient();
        var fileResponse = await anonymous.GetAsync(relativeUrl);
        fileResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await fileResponse.Content.ReadAsStringAsync()).Should().Be("hello-from-test");

        var tamperedUrl = relativeUrl[..^1] + (relativeUrl[^1] == 'a' ? 'b' : 'a');
        var tamperedResponse = await anonymous.GetAsync(tamperedUrl);
        tamperedResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
