using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Notifications;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Notifications;

/// <summary>
/// Редизайн навигации: NotificationSendingService теперь фан-аутит на ВСЕ зарегистрированные
/// INotificationSender (Telegram и/или Web Push могут быть настроены одновременно), а не на один.
/// </summary>
public class NotificationSendingServiceTests : SqliteTestBase
{
    private static Notification NewNotification() => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        FamilyId = Guid.NewGuid(),
        Type = NotificationType.BirthdayUpcoming,
        Title = "Тест",
        Body = "Тест",
        RelatedEntityId = Guid.NewGuid(),
        DedupKey = Guid.NewGuid().ToString(),
        CreatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task TrySendAsync_CallsAllRegisteredSenders()
    {
        var telegram = Substitute.For<INotificationSender>();
        var webPush = Substitute.For<INotificationSender>();
        var sut = new NotificationSendingService(Db, [telegram, webPush], NullLogger<NotificationSendingService>.Instance);
        var notification = NewNotification();

        await sut.TrySendAsync(notification);

        await telegram.Received(1).SendAsync(notification, Arg.Any<CancellationToken>());
        await webPush.Received(1).SendAsync(notification, Arg.Any<CancellationToken>());
        notification.SentAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TrySendAsync_OneSenderThrows_OtherChannelStillReceivesIt()
    {
        var failing = Substitute.For<INotificationSender>();
        failing.SendAsync(Arg.Any<Notification>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("канал недоступен")));
        var healthy = Substitute.For<INotificationSender>();
        var sut = new NotificationSendingService(Db, [failing, healthy], NullLogger<NotificationSendingService>.Instance);
        var notification = NewNotification();

        await sut.TrySendAsync(notification);

        await healthy.Received(1).SendAsync(notification, Arg.Any<CancellationToken>());
        notification.SentAt.Should().NotBeNull("сбой одного канала не должен помешать доставке по остальным");
    }

    [Fact]
    public async Task TrySendAsync_NoSendersRegistered_StillMarksSentAt()
    {
        var sut = new NotificationSendingService(Db, [], NullLogger<NotificationSendingService>.Instance);
        var notification = NewNotification();

        await sut.TrySendAsync(notification);

        notification.SentAt.Should().NotBeNull();
    }
}
