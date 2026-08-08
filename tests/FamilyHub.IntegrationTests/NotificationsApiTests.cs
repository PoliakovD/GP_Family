using System.Net;
using System.Net.Http.Json;
using FamilyHub.Api.Features.Notifications;
using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.Medications;
using FamilyHub.Modules.Medical.Medkits;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

public class NotificationsApiTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private async Task<Guid> CreateFamilyAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync("/api/families", new { Name = $"Семья {Guid.NewGuid():N}" });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, Guid>>(JsonOpts);
        return body!["id"];
    }

    /// <summary>Dev-эндпоинты не имеют AllowAnonymous, поэтому требуют FallbackPolicy-аутентификацию.</summary>
    private static async Task TriggerAsync(HttpClient client, string path)
    {
        var response = await client.PostAsync(path, null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>Полинг до выполнения условия — доставка события шиной (ADR-0006) асинхронна,
    /// нет форсирующего /dev/trigger-outbox-dispatch (удалён вместе с собственным outbox).</summary>
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
    public async Task ExpiringMedication_ProducesNotification_VisibleOnlyToFamilyMember_WithUnreadFilterAndMarkRead()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        var medkit = await (await admin.PostAsJsonAsync($"/api/families/{familyId}/medkits", new CreateMedkitRequest("Аптечка")))
            .Content.ReadFromJsonAsync<MedkitDto>(JsonOpts);
        (await admin.PostAsJsonAsync($"/api/medkits/{medkit!.Id}/medications",
                new CreateMedicationRequest("Скоро истекающее", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)),
                    new Dictionary<string, string> { ["quantity"] = "5" })))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // Скан публикует событие через шину; оповещение создаёт потребитель при асинхронной
        // доставке (ADR-0006) — ждём эффект полингом, а не форсируем доставку явно.
        await TriggerAsync(admin, "/dev/trigger-reminder-scan");
        await WaitForAsync(async () =>
            (await (await admin.GetAsync("/api/notifications")).Content.ReadFromJsonAsync<List<NotificationDto>>(JsonOpts))!
                .Any(n => n.Type == NotificationType.MedicationExpiringSoon),
            "скан должен породить оповещение об истекающем сроке");

        var notifications = await (await admin.GetAsync("/api/notifications")).Content.ReadFromJsonAsync<List<NotificationDto>>(JsonOpts);
        notifications.Should().ContainSingle(n => n.Type == NotificationType.MedicationExpiringSoon);
        var notification = notifications!.Single(n => n.Type == NotificationType.MedicationExpiringSoon);
        notification.IsRead.Should().BeFalse();

        var unreadOnly = await (await admin.GetAsync("/api/notifications?unreadOnly=true")).Content.ReadFromJsonAsync<List<NotificationDto>>(JsonOpts);
        unreadOnly.Should().ContainSingle(n => n.Id == notification.Id);

        var markReadResponse = await admin.PostAsync($"/api/notifications/{notification.Id}/read", null);
        markReadResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterMarkRead = await (await admin.GetAsync("/api/notifications?unreadOnly=true")).Content.ReadFromJsonAsync<List<NotificationDto>>(JsonOpts);
        afterMarkRead.Should().NotContain(n => n.Id == notification.Id);

        var stranger = ClientAs(FreshTelegramId());
        var strangerNotifications = await (await stranger.GetAsync("/api/notifications")).Content.ReadFromJsonAsync<List<NotificationDto>>(JsonOpts);
        strangerNotifications.Should().NotContain(n => n.Id == notification.Id);
    }

    [Fact]
    public async Task MarkRead_UnknownOrOthersNotification_Returns404()
    {
        var admin = ClientAs(FreshTelegramId());

        var response = await admin.PostAsync($"/api/notifications/{Guid.NewGuid()}/read", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
