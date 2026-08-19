using FamilyHub.Contracts.Events;
using FamilyHub.Infrastructure.Notifications;
using FamilyHub.TestUtils;
using FamilyHub.UnitTests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Notifications;

/// <summary>
/// Замена TelegramNotificationSenderTests после выноса бота (ADR-0008): SendAsync больше не
/// зовёт ITelegramBotClient напрямую, а публикует TelegramMessageRequestedEvent через
/// IDomainEventPublisher — сама отправка (и обработка ошибок Bot API) теперь на стороне
/// FamilyHub.TelegramBot (см. TelegramOutboundConsumerTests). Резолв TelegramId и ранний выход
/// при его отсутствии — та же логика, что была в TelegramNotificationSender.
/// </summary>
public class TelegramOutboundPublisherTests : SqliteTestBase
{
    private readonly RecordingDomainEventPublisher _publisher = new();
    private readonly ILogger<TelegramOutboundPublisher> _logger = Substitute.For<ILogger<TelegramOutboundPublisher>>();

    private TelegramOutboundPublisher CreateSut() => new(_publisher, Db, _logger);

    [Fact]
    public async Task SendAsync_KnownUser_PublishesMessageToTheirTelegramId()
    {
        var user = Db.AddUser();
        var (family, _) = Db.SeedFamilyWithAdmin();
        var notification = TestData.NewNotification(user.Id, family.Id, "dk-1");
        Db.Notifications.Add(notification);
        await Db.SaveChangesAsync();

        await CreateSut().SendAsync(notification);

        _publisher.Published.Should().ContainSingle().Which.Should().BeOfType<TelegramMessageRequestedEvent>()
            .Which.Should().BeEquivalentTo(new
            {
                ChatId = user.TelegramId,
                DedupKey = notification.DedupKey,
                WithMiniAppButton = true,
            });
        var published = (TelegramMessageRequestedEvent)_publisher.Published.Single();
        published.Text.Should().Contain(notification.Title).And.Contain(notification.Body);
    }

    [Fact]
    public async Task SendAsync_UserWithoutTelegramId_DoesNotPublish()
    {
        // Notification.UserId — required FK на Users, поэтому "юзера вообще нет" не воспроизвести
        // без нарушения FK (как и в проде). Эмулируем оборонительную ветку TelegramId==0 через
        // запись с явно нулевым TelegramId — реальный пользователь существует, просто без Telegram.
        var orphan = TestData.NewUser();
        orphan.TelegramId = 0;
        Db.Users.Add(orphan);
        var (family, _) = Db.SeedFamilyWithAdmin();
        var notification = TestData.NewNotification(orphan.Id, family.Id, "dk-1");
        Db.Notifications.Add(notification);
        await Db.SaveChangesAsync();

        await CreateSut().SendAsync(notification);

        _publisher.Published.Should().BeEmpty();
    }
}
