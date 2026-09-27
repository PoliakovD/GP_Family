using System.Net;
using System.Net.Http.Json;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>«Кто видит моё здоровье» (ADR-0017) — настройка per-категорийных грантов доступа к
/// дневнику/приёму/прививкам.</summary>
public class HealthSharesApiTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private record CreateFamilyResponseDto(Guid Id);
    private record CreateInviteResponseDto(Guid Id, string Code);
    private record PendingMemberDto(Guid UserId);
    private record MeDto(Guid UserId);
    private record GrantDto(Guid ViewerUserId, string Name, int Categories);
    private record SharedWithMeDto(Guid OwnerUserId, string Name, int Categories);

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

    [Fact]
    public async Task Mine_ListsActiveFamilyMember_DefaultingToNone()
    {
        var (_, owner, viewer) = await CreateFamilyWithActiveMemberAsync();
        var viewerMe = await viewer.GetFromJsonAsync<MeDto>("/api/auth/me", JsonOpts);

        var mine = await owner.GetFromJsonAsync<List<GrantDto>>("/api/health-shares/mine", JsonOpts);

        mine.Should().ContainSingle(g => g.ViewerUserId == viewerMe!.UserId && g.Categories == 0);
    }

    [Fact]
    public async Task Set_ThenSharedWithMe_ReflectsTheGrant_ZeroRevokesIt()
    {
        var (_, owner, viewer) = await CreateFamilyWithActiveMemberAsync();
        var ownerMe = await owner.GetFromJsonAsync<MeDto>("/api/auth/me", JsonOpts);
        var viewerMe = await viewer.GetFromJsonAsync<MeDto>("/api/auth/me", JsonOpts);

        (await owner.PutAsJsonAsync($"/api/health-shares/mine/{viewerMe!.UserId}",
                new { categories = (int)(HealthShareCategory.Diary | HealthShareCategory.Intake) }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var shared = await viewer.GetFromJsonAsync<List<SharedWithMeDto>>("/api/health-shares/shared-with-me", JsonOpts);
        shared.Should().ContainSingle(s => s.OwnerUserId == ownerMe!.UserId
            && s.Categories == (int)(HealthShareCategory.Diary | HealthShareCategory.Intake));

        (await owner.PutAsJsonAsync($"/api/health-shares/mine/{viewerMe.UserId}", new { categories = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await viewer.GetFromJsonAsync<List<SharedWithMeDto>>("/api/health-shares/shared-with-me", JsonOpts)).Should().BeEmpty();
    }

    [Fact]
    public async Task Set_ForNonFamilyMember_IsBadRequest()
    {
        var owner = ClientAs(FreshTelegramId());
        var stranger = ClientAs(FreshTelegramId());
        var strangerMe = await stranger.GetFromJsonAsync<MeDto>("/api/auth/me", JsonOpts);

        (await owner.PutAsJsonAsync($"/api/health-shares/mine/{strangerMe!.UserId}", new { categories = (int)HealthShareCategory.Diary }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
