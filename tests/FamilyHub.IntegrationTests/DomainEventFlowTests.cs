using System.Net;
using System.Net.Http.Json;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.MedicalRecords;
using FamilyHub.Modules.Medical.Medications;
using FamilyHub.Modules.Medical.Medkits;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Сквозные инварианты шины (MassTransit InMemory + EF Core Outbox, ADR-0006): события из
/// бизнес-операций доходят до потребителей ровно один раз. Доставка теперь полностью
/// асинхронна — нет форсирующего dev-эндпоинта (UseBusOutbox будит delivery service сразу
/// после SaveChanges, иначе полинг по Messaging:Outbox:QueryDelay, ускоренному фабрикой до
/// 200мс) — все проверки идут через полинг-хелпер WaitForAsync. Проверка независимо от
/// брокера: Messaging:Kafka:Enabled=false в этой коллекции (см. FamilyHubWebFactory) —
/// Kafka-специфичный путь доставки покрывает отдельно KafkaBridgeFlowTests.
/// </summary>
public class DomainEventFlowTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
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

    private async Task<List<NotificationItemDto>> GetNotificationsAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/notifications")).Content.ReadFromJsonAsync<List<NotificationItemDto>>(JsonOpts))!;

    /// <summary>Полинг до выполнения условия — для проверок, где доставку делает фоновая шина.</summary>
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

    [Fact]
    public async Task UserLeavesFamily_MedicalShareIsRevoked_AndAdminIsNotified()
    {
        // Критичный инвариант: выход из семьи → отзыв FamilyMedicalShare через событие шины.
        var (familyId, admin, member, _) = await CreateFamilyWithActiveMemberAsync();
        await member.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest(DateOnly.FromDateTime(DateTime.UtcNow), null, null, null));
        (await member.PostAsJsonAsync("/api/medical-records/share", new ShareFamilyRequest(familyId)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var sharesBefore = await (await member.GetAsync("/api/medical-records/shares"))
            .Content.ReadFromJsonAsync<List<Guid>>(JsonOpts);
        sharesBefore.Should().Contain(familyId);

        (await member.PostAsync($"/api/families/{familyId}/leave", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await WaitForAsync(async () =>
        {
            var shares = await (await member.GetAsync("/api/medical-records/shares"))
                .Content.ReadFromJsonAsync<List<Guid>>(JsonOpts);
            return !shares!.Contains(familyId);
        }, "Medical-потребитель UserLeftFamilyEvent должен отозвать шару ушедшего");

        await WaitForAsync(async () =>
            (await GetNotificationsAsync(admin)).Any(n => n.Type == NotificationType.MemberLeft),
            "админ должен получить оповещение MemberLeft");
    }

    [Fact]
    public async Task MemberApproved_NotifiesExistingMembers_ButNotTheNewcomer()
    {
        var (_, admin, member, _) = await CreateFamilyWithActiveMemberAsync();

        await WaitForAsync(async () =>
            (await GetNotificationsAsync(admin)).Any(n => n.Type == NotificationType.MemberApproved),
            "существующие члены семьи должны узнать о новом участнике");

        (await GetNotificationsAsync(member))
            .Should().NotContain(n => n.Type == NotificationType.MemberApproved, "сам новичок не оповещается");
    }

    [Fact]
    public async Task ShareTwice_PublishesSingleEvent_AndNotifiesMembersOnce()
    {
        // Раньше проверяли напрямую по строке в db.OutboxMessages ("повторный share не публикует
        // событие") — та таблица заменена MassTransit-схемой без прикладного смысла для этой
        // проверки (см. ADR-0006). Инвариант "ровно одно событие" теперь наблюдаем только через
        // его единственный эффект — ровно одно оповещение у получателей.
        var (familyId, admin, member, _) = await CreateFamilyWithActiveMemberAsync();
        await member.PostAsJsonAsync("/api/medical-records",
            new CreateMedicalRecordRequest(DateOnly.FromDateTime(DateTime.UtcNow), null, null, null));

        (await member.PostAsJsonAsync("/api/medical-records/share", new ShareFamilyRequest(familyId)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await member.PostAsJsonAsync("/api/medical-records/share", new ShareFamilyRequest(familyId)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        await WaitForAsync(async () =>
            (await GetNotificationsAsync(admin)).Any(n => n.Type == NotificationType.MedicalRecordShared),
            "члены семьи должны получить оповещение о выданном доступе");

        // Грейс-период: если бы повторный share всё же опубликовал второе событие, его доставка
        // (асинхронная, не синхронизированная предыдущим WaitForAsync) успела бы прийти здесь.
        await Task.Delay(500);
        (await GetNotificationsAsync(admin))
            .Count(n => n.Type == NotificationType.MedicalRecordShared)
            .Should().Be(1, "повторный share при уже существующей шаре события не публикует");
    }

    [Fact]
    public async Task ProcessedEvent_IsNotDeliveredTwice()
    {
        var (_, admin, _, _) = await CreateFamilyWithActiveMemberAsync();

        await WaitForAsync(async () =>
            (await GetNotificationsAsync(admin)).Any(n => n.Type == NotificationType.MemberApproved),
            "первая доставка должна создать оповещение");

        // exactly-once — теперь гарантия топологии шины (один receive endpoint на потребителя,
        // без ретрая всей исходной публикации), а не нашего кода: явного "передоставить" API у
        // MassTransit нет, поэтому вместо повторного форс-диспатча проверяем отсутствие
        // спонтанного дубля за грейс-период.
        await Task.Delay(500);
        (await GetNotificationsAsync(admin))
            .Count(n => n.Type == NotificationType.MemberApproved)
            .Should().Be(1, "exactly-once: сообщение не доставляется потребителю повторно");
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

        // Двойной скан: префильтр джобы + DedupKey потребителя в сумме дают ровно одно
        // оповещение. Раньше синхронизирующим барьером между прогонами служил форс-диспатч —
        // теперь его роль явно берёт на себя WaitForAsync (публикация асинхронна, второй скан
        // не должен стартовать раньше, чем DedupKey-строка первого попадёт в БД, иначе его
        // собственный префильтр не увидит её и опубликует дубль события).
        (await admin.PostAsync("/dev/trigger-reminder-scan", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        await WaitForAsync(async () =>
            (await GetNotificationsAsync(admin)).Any(n => n.Type == NotificationType.MedicationExpiringSoon
                && n.Title.Contains("Аспирин")),
            "скан должен породить оповещение об истекающем сроке");

        (await admin.PostAsync("/dev/trigger-reminder-scan", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Грейс-период — как и выше, ловит гипотетический поздний дубль от второго скана.
        await Task.Delay(500);
        (await GetNotificationsAsync(admin))
            .Count(n => n.Type == NotificationType.MedicationExpiringSoon && n.Title.Contains("Аспирин"))
            .Should().Be(1, $"медикамент {medication!.Id}: повторный скан не создаёт дублей");
    }
}
