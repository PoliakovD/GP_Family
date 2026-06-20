using System.Net;
using System.Net.Http.Json;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>Сквозной HTTP-сценарий: создание семьи -> приглашение -> заявка -> одобрение.</summary>
public class FamiliesAndInvitesFlowTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private record CreateFamilyResponseDto(Guid Id);
    private record FamilySummaryDto(Guid Id, string Name, FamilyRole MyRole, MemberStatus MyStatus);
    private record CreateInviteResponseDto(Guid Id, string Code, int MaxUses, DateTime? ExpiresAt);
    private record RedeemResponseDto(string Status);
    private record PendingMemberDto(Guid UserId, FamilyRole Role, DateTime JoinedAt);

    private async Task<(Guid FamilyId, HttpClient AdminClient)> CreateFamilyAsAdminAsync()
    {
        var admin = ClientAs(FreshTelegramId());
        var createResponse = await admin.PostAsJsonAsync("/api/families", new { Name = $"Семья {Guid.NewGuid():N}" });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<CreateFamilyResponseDto>(JsonOpts);
        return (created!.Id, admin);
    }

    [Fact]
    public async Task CreateFamily_AppearsInMyFamiliesList_AsAdminAndActive()
    {
        var (familyId, admin) = await CreateFamilyAsAdminAsync();

        var listResponse = await admin.GetAsync("/api/families");
        var families = await listResponse.Content.ReadFromJsonAsync<List<FamilySummaryDto>>(JsonOpts);

        families.Should().ContainSingle(f => f.Id == familyId)
            .Which.Should().BeEquivalentTo(new { MyRole = FamilyRole.Admin, MyStatus = MemberStatus.Active }, o => o.ExcludingMissingMembers());
    }

    [Fact]
    public async Task FullInviteFlow_LinkInvite_PendingApproval_ThenApprove_GrantsAccess()
    {
        var (familyId, admin) = await CreateFamilyAsAdminAsync();

        var inviteResponse = await admin.PostAsJsonAsync($"/api/families/{familyId}/invites",
            new CreateInviteRequest(TargetUserId: null, AssignedRole: FamilyRole.Member, MaxUses: 5, ExpiresAt: null));
        inviteResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var invite = await inviteResponse.Content.ReadFromJsonAsync<CreateInviteResponseDto>(JsonOpts);

        var applicant = ClientAs(FreshTelegramId());
        var redeemResponse = await applicant.PostAsync($"/api/invites/{invite!.Code}/redeem", null);
        redeemResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var redeemed = await redeemResponse.Content.ReadFromJsonAsync<RedeemResponseDto>(JsonOpts);
        redeemed!.Status.Should().Be("pending_approval");

        var pendingResponse = await admin.GetAsync($"/api/families/{familyId}/pending");
        var pending = await pendingResponse.Content.ReadFromJsonAsync<List<PendingMemberDto>>(JsonOpts);
        var applicantUserId = pending.Should().ContainSingle().Which.UserId;

        // До одобрения заявитель в семье числится, но Pending — список семей видит, ресурсы семьи — нет
        // (это покрыто отдельными module-эндпоинт-тестами; здесь фиксируем сам факт approve).
        var approveResponse = await admin.PostAsync($"/api/families/{familyId}/members/{applicantUserId}/approve", null);
        approveResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var applicantFamilies = await (await applicant.GetAsync("/api/families")).Content.ReadFromJsonAsync<List<FamilySummaryDto>>(JsonOpts);
        applicantFamilies.Should().ContainSingle(f => f.Id == familyId)
            .Which.MyStatus.Should().Be(MemberStatus.Active);
    }

    [Fact]
    public async Task CreateInvite_AsNonAdminMember_Returns403()
    {
        var (familyId, admin) = await CreateFamilyAsAdminAsync();

        // Заводим обычного члена семьи (не админа) через тот же путь: ссылочный инвайт + approve.
        var inviteResponse = await admin.PostAsJsonAsync($"/api/families/{familyId}/invites",
            new CreateInviteRequest(TargetUserId: null, AssignedRole: FamilyRole.Member, MaxUses: 1, ExpiresAt: null));
        var invite = await inviteResponse.Content.ReadFromJsonAsync<CreateInviteResponseDto>(JsonOpts);
        var member = ClientAs(FreshTelegramId());
        await member.PostAsync($"/api/invites/{invite!.Code}/redeem", null);
        var pending = await (await admin.GetAsync($"/api/families/{familyId}/pending")).Content.ReadFromJsonAsync<List<PendingMemberDto>>(JsonOpts);
        var memberUserId = pending!.Single().UserId;
        await admin.PostAsync($"/api/families/{familyId}/members/{memberUserId}/approve", null);

        var forbiddenResponse = await member.PostAsJsonAsync($"/api/families/{familyId}/invites",
            new CreateInviteRequest(TargetUserId: null, AssignedRole: FamilyRole.Member, MaxUses: 1, ExpiresAt: null));

        forbiddenResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
