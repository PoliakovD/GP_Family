using System.Net;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Notifications;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WebPush;
using Xunit;
using DomainPushSubscription = FamilyHub.Domain.Entities.PushSubscription;

namespace FamilyHub.UnitTests.Infrastructure.Notifications;

/// <summary>
/// Редизайн навигации, ADR-0004: доставка через Web Push должна (1) слать обобщённый payload,
/// НИКОГДА не реальные Title/Body записи (могут содержать имена, ADR-0002), (2) чистить протухшие
/// (404/410) подписки, (3) не давать сбою одной подписки заблокировать остальные.
/// </summary>
public class WebPushNotificationSenderTests : SqliteTestBase
{
    private readonly IWebPushClient _client = Substitute.For<IWebPushClient>();
    private readonly WebPushNotificationSender _sut;

    public WebPushNotificationSenderTests()
    {
        _sut = new WebPushNotificationSender(_client, Db, NullLogger<WebPushNotificationSender>.Instance);
    }

    private static Notification NewNotification(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        FamilyId = Guid.NewGuid(),
        Type = NotificationType.MedicationExpiringSoon,
        Title = "Совершенно секретное имя пациента Иванов",
        Body = "Диагноз и подробности — не должны утечь в push",
        RelatedEntityId = Guid.NewGuid(),
        DedupKey = Guid.NewGuid().ToString(),
        CreatedAt = DateTime.UtcNow,
    };

    private DomainPushSubscription AddSubscription(Guid userId, string endpoint)
    {
        var subscription = new DomainPushSubscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            // Sender не использует EndpointHash (это PushSubscriptionService.SubscribeAsync/UnsubscribeAsync) —
            // для теста достаточно любого уникального значения, не обязательно реального SHA-256.
            EndpointHash = Guid.NewGuid().ToString("N"),
            Endpoint = endpoint,
            P256dh = "p256dh",
            Auth = "auth",
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
        };
        Db.PushSubscriptions.Add(subscription);
        return subscription;
    }

    [Fact]
    public async Task SendAsync_NoSubscriptions_DoesNothing()
    {
        var owner = Db.AddUser();
        await Db.SaveChangesAsync();

        await _sut.SendAsync(NewNotification(owner.Id));

        await _client.DidNotReceiveWithAnyArgs().SendNotificationAsync(default!, default);
    }

    [Fact]
    public async Task SendAsync_Payload_IsGeneric_NeverContainsRealTitleOrBody()
    {
        var owner = Db.AddUser();
        AddSubscription(owner.Id, "https://fcm.googleapis.com/fcm/send/device-1");
        await Db.SaveChangesAsync();

        string? capturedPayload = null;
        _client.SendNotificationAsync(Arg.Any<WebPush.PushSubscription>(), Arg.Do<string>(p => capturedPayload = p),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var notification = NewNotification(owner.Id);
        await _sut.SendAsync(notification);

        capturedPayload.Should().NotBeNull();
        capturedPayload.Should().NotContain("Иванов", "payload не должен раскрывать реальный Title (ADR-0002/ADR-0004)");
        capturedPayload.Should().NotContain("Диагноз", "payload не должен раскрывать реальный Body");
        capturedPayload.Should().Contain("\"url\":\"/notifications\"", "клик должен вести в уже аутентифицированный инбокс");
    }

    [Fact]
    public async Task SendAsync_SendsToAllSubscriptionsOfUser()
    {
        var owner = Db.AddUser();
        AddSubscription(owner.Id, "https://fcm.googleapis.com/fcm/send/device-1");
        AddSubscription(owner.Id, "https://updates.push.services.mozilla.com/wpush/v2/device-2");
        await Db.SaveChangesAsync();

        _client.SendNotificationAsync(Arg.Any<WebPush.PushSubscription>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.SendAsync(NewNotification(owner.Id));

        await _client.Received(2).SendNotificationAsync(Arg.Any<WebPush.PushSubscription>(), Arg.Any<string>(),
            Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_ExpiredSubscription_Gone410_IsRemoved_OthersUnaffected()
    {
        var owner = Db.AddUser();
        var expired = AddSubscription(owner.Id, "https://fcm.googleapis.com/fcm/send/expired");
        var healthy = AddSubscription(owner.Id, "https://fcm.googleapis.com/fcm/send/healthy");
        await Db.SaveChangesAsync();

        _client.SendNotificationAsync(Arg.Any<WebPush.PushSubscription>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var subscription = callInfo.Arg<WebPush.PushSubscription>();
                if (subscription.Endpoint == expired.Endpoint)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Gone);
                    throw new WebPushException("expired", subscription, response);
                }

                return Task.CompletedTask;
            });

        await _sut.SendAsync(NewNotification(owner.Id));

        var remaining = await NewContext().PushSubscriptions.AsNoTracking().ToListAsync();
        remaining.Should().ContainSingle(s => s.Id == healthy.Id);
        remaining.Should().NotContain(s => s.Id == expired.Id);
    }

    [Fact]
    public async Task SendAsync_OneSubscriptionThrowsUnexpectedError_OthersStillReceiveNotification()
    {
        var owner = Db.AddUser();
        var broken = AddSubscription(owner.Id, "https://fcm.googleapis.com/fcm/send/broken");
        var healthy = AddSubscription(owner.Id, "https://fcm.googleapis.com/fcm/send/healthy");
        await Db.SaveChangesAsync();

        var sentTo = new List<string>();
        _client.SendNotificationAsync(Arg.Any<WebPush.PushSubscription>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var subscription = callInfo.Arg<WebPush.PushSubscription>();
                if (subscription.Endpoint == broken.Endpoint)
                    throw new InvalidOperationException("сеть недоступна");

                sentTo.Add(subscription.Endpoint);
                return Task.CompletedTask;
            });

        await _sut.SendAsync(NewNotification(owner.Id));

        sentTo.Should().ContainSingle().Which.Should().Be(healthy.Endpoint);
        // Неожиданная ошибка (не 404/410) НЕ должна удалить подписку — только протухшие чистим.
        var remaining = await NewContext().PushSubscriptions.AsNoTracking().ToListAsync();
        remaining.Should().Contain(s => s.Id == broken.Id);
    }

    // ── Кастомизация payload'а по типу уведомления (ADR-0015) ────────────────

    private WebPushNotificationSender WithCustomizer(IPushPayloadCustomizer customizer) =>
        new(_client, Db, NullLogger<WebPushNotificationSender>.Instance, [customizer]);

    private string? CapturePayload()
    {
        string? captured = null;
        _client.SendNotificationAsync(Arg.Any<WebPush.PushSubscription>(), Arg.Do<string>(p => captured = p),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return captured;
    }

    [Fact]
    public async Task SendAsync_CustomizedType_UsesCustomizerPayload_StillNeverRealTitleOrBody()
    {
        var owner = Db.AddUser();
        AddSubscription(owner.Id, "https://fcm.googleapis.com/fcm/send/device-1");
        await Db.SaveChangesAsync();
        var customizer = Substitute.For<IPushPayloadCustomizer>();
        customizer.CanHandle(NotificationType.MedicationDoseDue).Returns(true);
        customizer.BuildAsync(Arg.Any<Notification>(), Arg.Any<CancellationToken>()).Returns(new PushCustomization(
            Body: "Время принять лекарство · 14:00", Tag: "dose-1", Actions: [new PushActionButton("taken", "Принял")]));
        string? payload = null;
        _client.SendNotificationAsync(Arg.Any<WebPush.PushSubscription>(), Arg.Do<string>(p => payload = p),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var notification = NewNotification(owner.Id);
        notification.Type = NotificationType.MedicationDoseDue;

        await WithCustomizer(customizer).SendAsync(notification);

        using var doc = System.Text.Json.JsonDocument.Parse(payload!); // кириллица в JSON экранируется — сравниваем разобранное
        var n = doc.RootElement.GetProperty("notification");
        n.GetProperty("body").GetString().Should().Be("Время принять лекарство · 14:00");
        n.GetProperty("tag").GetString().Should().Be("dose-1");
        payload.Should().NotContain("Иванов").And.NotContain("Диагноз");
    }

    [Fact]
    public async Task SendAsync_CustomizerReturnsNull_SendsNothing()
    {
        var owner = Db.AddUser();
        AddSubscription(owner.Id, "https://fcm.googleapis.com/fcm/send/device-1");
        await Db.SaveChangesAsync();
        var customizer = Substitute.For<IPushPayloadCustomizer>();
        customizer.CanHandle(Arg.Any<NotificationType>()).Returns(true);
        customizer.BuildAsync(Arg.Any<Notification>(), Arg.Any<CancellationToken>()).Returns((PushCustomization?)null);

        await WithCustomizer(customizer).SendAsync(NewNotification(owner.Id));

        await _client.DidNotReceiveWithAnyArgs().SendNotificationAsync(default!, default);
    }

    [Fact]
    public async Task SendAsync_TypeWithoutCustomizer_KeepsGenericPayload()
    {
        var owner = Db.AddUser();
        AddSubscription(owner.Id, "https://fcm.googleapis.com/fcm/send/device-1");
        await Db.SaveChangesAsync();
        var customizer = Substitute.For<IPushPayloadCustomizer>();
        customizer.CanHandle(Arg.Any<NotificationType>()).Returns(false);
        string? payload = null;
        _client.SendNotificationAsync(Arg.Any<WebPush.PushSubscription>(), Arg.Do<string>(p => payload = p),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await WithCustomizer(customizer).SendAsync(NewNotification(owner.Id));

        using var doc = System.Text.Json.JsonDocument.Parse(payload!);
        doc.RootElement.GetProperty("notification").GetProperty("body").GetString().Should().Be("Новое уведомление");
        payload.Should().Contain("\"url\":\"/notifications\"");
        await customizer.DidNotReceiveWithAnyArgs().BuildAsync(default!, default);
    }
}
