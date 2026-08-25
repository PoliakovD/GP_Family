using System.Net;
using System.Net.Http.Json;
using FamilyHub.Api.Features.Families;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    private record CurrentMemberDto(
        Guid Id, string? LastName, string? FirstName, string? MiddleName, string? Username, DateTime JoinedAt, FamilyRole Role);

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
    public async Task MyFamiliesList_OrdersAdminFamiliesBeforeMemberFamilies()
    {
        // Пользователь — админ своей семьи "Я-Семья" и обычный участник чужой "Ай-Семья"
        // (имя выбрано так, чтобы при сортировке по одному только имени "Ай-Семья" оказалась
        // раньше — так тест доказывает, что роль важнее имени, а не совпадает с ним случайно).
        var user = ClientAs(FreshTelegramId());
        var ownFamilyResponse = await user.PostAsJsonAsync("/api/families", new { Name = "Я-Семья" });
        var ownFamily = await ownFamilyResponse.Content.ReadFromJsonAsync<CreateFamilyResponseDto>(JsonOpts);

        var otherAdmin = ClientAs(FreshTelegramId());
        var otherFamilyResponse = await otherAdmin.PostAsJsonAsync("/api/families", new { Name = "Ай-Семья" });
        var otherFamily = await otherFamilyResponse.Content.ReadFromJsonAsync<CreateFamilyResponseDto>(JsonOpts);
        var inviteResponse = await otherAdmin.PostAsJsonAsync($"/api/families/{otherFamily!.Id}/invites",
            new CreateInviteRequest(TargetUserId: null, AssignedRole: FamilyRole.Member, MaxUses: 1, ExpiresAt: null));
        var invite = await inviteResponse.Content.ReadFromJsonAsync<CreateInviteResponseDto>(JsonOpts);
        await user.PostAsync($"/api/invites/{invite!.Code}/redeem", null);
        var pending = await (await otherAdmin.GetAsync($"/api/families/{otherFamily.Id}/pending")).Content.ReadFromJsonAsync<List<PendingMemberDto>>(JsonOpts);
        await otherAdmin.PostAsync($"/api/families/{otherFamily.Id}/members/{pending!.Single().UserId}/approve", null);

        var families = await (await user.GetAsync("/api/families")).Content.ReadFromJsonAsync<List<FamilySummaryDto>>(JsonOpts);

        families.Should().HaveCount(2);
        families![0].Id.Should().Be(ownFamily!.Id, "семья, где пользователь админ, должна идти первой");
        families[0].MyRole.Should().Be(FamilyRole.Admin);
        families[1].Id.Should().Be(otherFamily.Id);
        families[1].MyRole.Should().Be(FamilyRole.Member);
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

    // Регрессия на broken access control (аудит 2026-08-02, находка [02]): GetFamilyMembersAsync
    // раньше не принимал userId и не проверял членство вообще — любой аутентифицированный
    // пользователь мог прочитать состав ЛЮБОЙ семьи по известному GUID.
    [Fact]
    public async Task GetCurrentMembers_AsOutsider_Returns403()
    {
        var (familyId, _) = await CreateFamilyAsAdminAsync();
        var outsider = ClientAs(FreshTelegramId());

        var response = await outsider.GetAsync($"/api/families/{familyId}/current");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetCurrentMembers_AsActiveAdmin_Returns200AndIncludesSelf()
    {
        var (familyId, admin) = await CreateFamilyAsAdminAsync();

        var response = await admin.GetAsync($"/api/families/{familyId}/current");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var members = await response.Content.ReadFromJsonAsync<List<CurrentMemberDto>>(JsonOpts);
        members.Should().ContainSingle(m => m.Role == FamilyRole.Admin);
    }

    [Fact]
    public async Task GetCurrentMembers_AsPendingApplicant_Returns403()
    {
        var (familyId, admin) = await CreateFamilyAsAdminAsync();
        var inviteResponse = await admin.PostAsJsonAsync($"/api/families/{familyId}/invites",
            new CreateInviteRequest(TargetUserId: null, AssignedRole: FamilyRole.Member, MaxUses: 1, ExpiresAt: null));
        var invite = await inviteResponse.Content.ReadFromJsonAsync<CreateInviteResponseDto>(JsonOpts);

        var applicant = ClientAs(FreshTelegramId());
        await applicant.PostAsync($"/api/invites/{invite!.Code}/redeem", null);

        // Заявитель формально состоит в семье (PendingApproval), но ещё не Active —
        // HasRoleAsync требует Active, поэтому доступа к составу семьи у него быть не должно.
        var response = await applicant.GetAsync($"/api/families/{familyId}/current");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // Регрессия на аудит module-review-2026-08-02/02, находка 4: без лимита пользователь мог
    // насоздавать сколько угодно семей подряд (спам/захламление БД).
    [Fact]
    public async Task CreateFamily_AtLimit_Returns409_AndDoesNotCreateExtraFamily()
    {
        var admin = ClientAs(FreshTelegramId());
        var me = await (await admin.GetAsync("/api/auth/me")).Content.ReadFromJsonAsync<MeDto>(JsonOpts);

        // Сидируем напрямую через БД (быстрее, чем 25 HTTP round-trip'ов) — семьи, где
        // пользователь Admin, ровно то, что считает FamilyService.MaxFamiliesPerUser.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < FamilyService.MaxFamiliesPerUser; i++)
            {
                var family = new Family { Id = Guid.NewGuid(), Name = $"Seed {i}", PlanType = PlanType.Free, CreatedAt = DateTime.UtcNow };
                db.Families.Add(family);
                db.FamilyMembers.Add(new FamilyMember
                {
                    Id = Guid.NewGuid(),
                    FamilyId = family.Id,
                    UserId = me!.UserId,
                    Role = FamilyRole.Admin,
                    Status = MemberStatus.Active,
                    JoinedAt = DateTime.UtcNow,
                });
            }
            await db.SaveChangesAsync();
        }

        var response = await admin.PostAsJsonAsync("/api/families", new { Name = "One too many" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var families = await (await admin.GetAsync("/api/families")).Content.ReadFromJsonAsync<List<FamilySummaryDto>>(JsonOpts);
        families.Should().HaveCount(FamilyService.MaxFamiliesPerUser, "лишняя семья создаваться не должна была");
    }

    private record MeDto(Guid UserId);
}
