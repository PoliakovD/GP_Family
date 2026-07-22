using System.Net;
using System.Net.Http.Json;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.MedicalRecords;
using FamilyHub.Modules.Medical.Medications;
using FamilyHub.Modules.Medical.Medkits;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Сквозные инварианты этапа 1 (MediatR + outbox): события из бизнес-операций доходят до
/// хендлеров ровно один раз. Доставку форсируем dev-эндпоинтом /dev/trigger-outbox-dispatch
/// (детерминизм), а фоновый OutboxDispatcher с ускоренным poll'ом (500мс из фабрики)
/// покрывается полинг-хелпером WaitForAsync.
/// </summary>
public class OutboxEventFlowTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private record CreateFamilyResponseDto(Guid Id);
    private record CreateInviteResponseDto(Guid Id, string Code);
    private record PendingMemberDto(Guid UserId);
    private record NotificationItemDto(Guid Id, NotificationType Type, string Title);

    private async Task<(Guid FamilyId, HttpClient Admin, HttpClient Member, Guid MemberUserId)> CreateFamilyWithActiveMemberAsync()
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
        var memberUserId = pending!.Single().UserId;
        await admin.PostAsync($"/api/families/{family.Id}/members/{memberUserId}/approve", null);

        return (family.Id, admin, member, memberUserId);
    }

    private static async Task DispatchOutboxAsync(HttpClient client)
    {
        var response = await client.PostAsync("/dev/trigger-outbox-dispatch", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<List<NotificationItemDto>> GetNotificationsAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/notifications")).Content.ReadFromJsonAsync<List<NotificationItemDto>>(JsonOpts))!;

    /// <summary>Полинг до выполнения условия — для проверок, где доставку делает фоновый цикл.</summary>
    private static async Task WaitForAsync(Func<Task<bool>> condition, string because, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(200);
        }

        (await condition()).Should().BeTrue(because);
    }

    private IServiceScope NewDbScope() => Factory.Services.CreateScope();

    [Fact]
    public async Task UserLeavesFamily_MedicalShareIsRevoked_AndAdminIsNotified()
    {
        // Критичный инвариант этапа 1: выход из семьи → отзыв FamilyMedicalShare через событие.
        var (familyId, admin, member, _) = await CreateFamilyWithActiveMemberAsync();
        await member.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest("Пациент", DateOnly.FromDateTime(DateTime.UtcNow), null, null, null));
        (await member.PostAsJsonAsync("/api/medical-records/share", new ShareFamilyRequest(familyId)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var sharesBefore = await (await member.GetAsync("/api/medical-records/shares"))
            .Content.ReadFromJsonAsync<List<Guid>>(JsonOpts);
        sharesBefore.Should().Contain(familyId);

        (await member.PostAsync($"/api/families/{familyId}/leave", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await DispatchOutboxAsync(admin);

        await WaitForAsync(async () =>
        {
            var shares = await (await member.GetAsync("/api/medical-records/shares"))
                .Content.ReadFromJsonAsync<List<Guid>>(JsonOpts);
            return !shares!.Contains(familyId);
        }, "Medical-хендлер UserLeftFamilyEvent должен отозвать шару ушедшего");

        await WaitForAsync(async () =>
            (await GetNotificationsAsync(admin)).Any(n => n.Type == NotificationType.MemberLeft),
            "админ должен получить оповещение MemberLeft");
    }

    [Fact]
    public async Task MemberApproved_NotifiesExistingMembers_ButNotTheNewcomer()
    {
        var (_, admin, member, _) = await CreateFamilyWithActiveMemberAsync();
        await DispatchOutboxAsync(admin);

        await WaitForAsync(async () =>
            (await GetNotificationsAsync(admin)).Any(n => n.Type == NotificationType.MemberApproved),
            "существующие члены семьи должны узнать о новом участнике");

        (await GetNotificationsAsync(member))
            .Should().NotContain(n => n.Type == NotificationType.MemberApproved, "сам новичок не оповещается");
    }

    [Fact]
    public async Task ShareTwice_PublishesSingleEvent_AndNotifiesMembersOnce()
    {
        var (familyId, admin, member, _) = await CreateFamilyWithActiveMemberAsync();
        await member.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest("Пациент", DateOnly.FromDateTime(DateTime.UtcNow), null, null, null));

        (await member.PostAsJsonAsync("/api/medical-records/share", new ShareFamilyRequest(familyId)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await member.PostAsJsonAsync("/api/medical-records/share", new ShareFamilyRequest(familyId)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = NewDbScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Contains по Payload — на клиенте: LIKE по jsonb-колонке Postgres не поддерживает.
        var shareEvents = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.Type == nameof(MedicalRecordSharedEvent))
            .ToListAsync();
        shareEvents.Count(m => m.Payload.Contains(familyId.ToString()))
            .Should().Be(1, "повторный share при уже существующей шаре события не публикует");

        await DispatchOutboxAsync(admin);
        await WaitForAsync(async () =>
            (await GetNotificationsAsync(admin)).Count(n => n.Type == NotificationType.MedicalRecordShared) == 1,
            "члены семьи получают ровно одно оповещение о выданном доступе");
    }

    [Fact]
    public async Task ProcessedEvent_IsNotDeliveredTwice()
    {
        var (familyId, admin, _, _) = await CreateFamilyWithActiveMemberAsync();
        await DispatchOutboxAsync(admin);

        await WaitForAsync(async () =>
            (await GetNotificationsAsync(admin)).Any(n => n.Type == NotificationType.MemberApproved),
            "первая доставка должна создать оповещение");

        // Повторные прогоны диспетчера не должны ни дублировать оповещения, ни трогать строку.
        await DispatchOutboxAsync(admin);
        await DispatchOutboxAsync(admin);

        (await GetNotificationsAsync(admin))
            .Count(n => n.Type == NotificationType.MemberApproved)
            .Should().Be(1, "exactly-once: повторная доставка идемпотентна");

        using var scope = NewDbScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var approvedEvents = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.Type == nameof(MemberApprovedEvent))
            .ToListAsync();
        var row = approvedEvents.Single(m => m.Payload.Contains(familyId.ToString()));
        row.ProcessedAt.Should().NotBeNull();
        row.Attempts.Should().Be(0);
        row.Error.Should().BeNull();
    }

    [Fact]
    public async Task ReminderScanTwice_CreatesSingleNotificationPerMedication()
    {
        var admin = ClientAs(FreshTelegramId());
        var family = await (await admin.PostAsJsonAsync("/api/families", new { Name = $"Семья {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<CreateFamilyResponseDto>(JsonOpts);
        var medkit = await (await admin.PostAsJsonAsync($"/api/families/{family!.Id}/medkits", new CreateMedkitRequest("Аптечка")))
            .Content.ReadFromJsonAsync<MedkitDto>(JsonOpts);
        var medication = await (await admin.PostAsJsonAsync($"/api/medkits/{medkit!.Id}/medications",
                new CreateMedicationRequest("Аспирин", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)), null)))
            .Content.ReadFromJsonAsync<MedicationDto>(JsonOpts);

        // Двойной скан: префильтр джобы + DedupKey хендлера в сумме дают ровно одно оповещение.
        (await admin.PostAsync("/dev/trigger-reminder-scan", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        await DispatchOutboxAsync(admin);
        (await admin.PostAsync("/dev/trigger-reminder-scan", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        await DispatchOutboxAsync(admin);

        await WaitForAsync(async () =>
            (await GetNotificationsAsync(admin)).Any(n => n.Type == NotificationType.MedicationExpiringSoon
                && n.Title.Contains("Аспирин")),
            "скан должен породить оповещение об истекающем сроке");

        (await GetNotificationsAsync(admin))
            .Count(n => n.Type == NotificationType.MedicationExpiringSoon && n.Title.Contains("Аспирин"))
            .Should().Be(1, $"медикамент {medication!.Id}: повторный скан не создаёт дублей");
    }
}
