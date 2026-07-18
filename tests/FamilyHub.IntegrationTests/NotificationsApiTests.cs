using System.Net;
using System.Net.Http.Json;
using FamilyHub.Api.Features.Notifications;
using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.Medications;
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

    /// <summary>/dev/trigger-reminder-scan не имеет AllowAnonymous, поэтому требует FallbackPolicy-аутентификацию.</summary>
    private async Task TriggerReminderScanAsync(HttpClient client)
    {
        var response = await client.PostAsync("/dev/trigger-reminder-scan", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExpiringMedication_ProducesNotification_VisibleOnlyToFamilyMember_WithUnreadFilterAndMarkRead()
    {
        var admin = ClientAs(FreshTelegramId());
        var familyId = await CreateFamilyAsync(admin);
        await admin.PostAsJsonAsync($"/api/families/{familyId}/medications",
            new CreateMedicationRequest("Скоро истекающее", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)),
                new Dictionary<string, string> { ["quantity"] = "5" }));

        await TriggerReminderScanAsync(admin);

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
