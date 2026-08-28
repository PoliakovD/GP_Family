using System.Net;
using System.Net.Http.Json;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.Medications;
using FamilyHub.Modules.Medical.Medkits;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>Редизайн v2 — GET /api/home/summary: агрегат «Требует внимания» (просроченные
/// лекарства, заявки на вступление, дни рождения) + блок «В порядке» одним запросом.</summary>
public class HomeSummaryTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private record HomeMedicationAlertDto(Guid MedicationId, Guid MedkitId, string MedkitName,
        Guid FamilyId, string FamilyName, string Name, DateOnly ExpiryDate, int DaysLeft, string Severity);
    private record HomeJoinRequestDto(Guid FamilyId, string FamilyName, Guid UserId,
        string? LastName, string? FirstName, string? MiddleName, string? Username, DateTime RequestedAt);
    private record HomeBirthdayItemDto(Guid FamilyId, string FamilyName, string PersonName,
        DateOnly Date, int DaysUntil, int TurningAge, int Source);
    private record HomeOkChipsDto(int MedicationsInDate, int MedicationsTotal, int AnalysesTotal, int AnalysesAbnormal, bool PushEnabled);
    private record HomeSummaryDto(
        string? GreetingName, DateOnly Today, int AttentionTotal, Guid? PrimaryFamilyId, string? PrimaryFamilyName,
        List<HomeMedicationAlertDto> Medications, List<HomeJoinRequestDto> JoinRequests,
        List<HomeBirthdayItemDto> Birthdays, HomeOkChipsDto Ok, int UnreadNotifications);
    private record CreateInviteResponseDto(Guid Id, string Code, int MaxUses, DateTime? ExpiresAt);
    private record RedeemResponseDto(string Status);

    private async Task<Guid> CreateFamilyAsync(HttpClient admin, string? name = null)
    {
        var response = await admin.PostAsJsonAsync("/api/families", new { Name = name ?? $"Семья {Guid.NewGuid():N}" });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, Guid>>(JsonOpts);
        return body!["id"];
    }

    [Fact]
    public async Task NewUser_NoFamilies_ReturnsEmptySummary()
    {
        var user = ClientAs(FreshTelegramId());

        var summary = await (await user.GetAsync("/api/home/summary")).Content.ReadFromJsonAsync<HomeSummaryDto>(JsonOpts);

        summary!.AttentionTotal.Should().Be(0);
        summary.Medications.Should().BeEmpty();
        summary.JoinRequests.Should().BeEmpty();
        summary.Birthdays.Should().BeEmpty();
        summary.PrimaryFamilyId.Should().BeNull();
        summary.Ok.MedicationsTotal.Should().Be(0);
    }

    [Fact]
    public async Task ExpiredMedication_AppearsInAttentionBlock_WithExpiredSeverity()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        var medkit = await (await admin.PostAsJsonAsync($"/api/families/{familyId}/medkits", new CreateMedkitRequest("Аптечка")))
            .Content.ReadFromJsonAsync<MedkitDto>(JsonOpts);
        var expired = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3));
        await admin.PostAsJsonAsync($"/api/medkits/{medkit!.Id}/medications",
            new CreateMedicationRequest("Просроченное", expired, new Dictionary<string, string>()));
        // Ещё одно, не попадающее в окно — не должно появиться в блоке внимания.
        await admin.PostAsJsonAsync($"/api/medkits/{medkit.Id}/medications",
            new CreateMedicationRequest("Свежее", DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), new Dictionary<string, string>()));

        var summary = await (await admin.GetAsync("/api/home/summary")).Content.ReadFromJsonAsync<HomeSummaryDto>(JsonOpts);

        var alert = summary!.Medications.Should().ContainSingle().Which;
        alert.Name.Should().Be("Просроченное");
        alert.Severity.Should().Be("expired");
        alert.DaysLeft.Should().BeLessThan(0);
        alert.FamilyId.Should().Be(familyId);

        summary.Ok.MedicationsTotal.Should().Be(2);
        summary.Ok.MedicationsInDate.Should().Be(1, "просроченное не считается «в сроке»");
        summary.AttentionTotal.Should().Be(1);
        summary.PrimaryFamilyId.Should().Be(familyId);
    }

    [Fact]
    public async Task JoinRequest_VisibleToAdmin_NotToApplicant_NotToUnrelatedUser()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        var inviteResponse = await admin.PostAsJsonAsync($"/api/families/{familyId}/invites",
            new CreateInviteRequest(TargetUserId: null, AssignedRole: FamilyRole.Member, MaxUses: 5, ExpiresAt: null));
        var invite = await inviteResponse.Content.ReadFromJsonAsync<CreateInviteResponseDto>(JsonOpts);

        var applicant = ClientAs(FreshTelegramId());
        var redeemed = await (await applicant.PostAsync($"/api/invites/{invite!.Code}/redeem", null))
            .Content.ReadFromJsonAsync<RedeemResponseDto>(JsonOpts);
        redeemed!.Status.Should().Be("pending_approval");

        var adminSummary = await (await admin.GetAsync("/api/home/summary")).Content.ReadFromJsonAsync<HomeSummaryDto>(JsonOpts);
        adminSummary!.JoinRequests.Should().ContainSingle(r => r.FamilyId == familyId);

        // Заявитель сам не видит свою заявку в /api/home/summary — эта заявка не в "его" списке
        // заявок (он не Admin нигде), только в списке /api/families сам факт PendingApproval.
        var applicantSummary = await (await applicant.GetAsync("/api/home/summary")).Content.ReadFromJsonAsync<HomeSummaryDto>(JsonOpts);
        applicantSummary!.JoinRequests.Should().BeEmpty();

        var stranger = ClientAs(FreshTelegramId());
        var strangerSummary = await (await stranger.GetAsync("/api/home/summary")).Content.ReadFromJsonAsync<HomeSummaryDto>(JsonOpts);
        strangerSummary!.JoinRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task UpcomingBirthday_WithinWindow_AppearsSortedByDaysUntil()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);

        var soon = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5));
        var farButInWindow = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(25));
        (await admin.PostAsJsonAsync($"/api/families/{familyId}/birthdays", new { PersonName = "Скоро", Date = soon }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await admin.PostAsJsonAsync($"/api/families/{familyId}/birthdays", new { PersonName = "Попозже", Date = farButInWindow }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var summary = await (await admin.GetAsync("/api/home/summary")).Content.ReadFromJsonAsync<HomeSummaryDto>(JsonOpts);

        summary!.Birthdays.Should().HaveCount(2);
        summary.Birthdays.Select(b => b.PersonName).Should().ContainInOrder("Скоро", "Попозже");
    }

    [Fact]
    public async Task UnreadNotifications_MatchesDedicatedUnreadCountEndpoint()
    {
        var admin = ClientAs(FreshTelegramId());

        var summary = await (await admin.GetAsync("/api/home/summary")).Content.ReadFromJsonAsync<HomeSummaryDto>(JsonOpts);
        var dedicated = await (await admin.GetAsync("/api/notifications/unread-count"))
            .Content.ReadFromJsonAsync<Dictionary<string, int>>(JsonOpts);

        summary!.UnreadNotifications.Should().Be(dedicated!["count"]);
    }
}
