using FamilyHub.Infrastructure.Notifications;
using FamilyHub.Infrastructure.Telegram;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Notifications;

public class TelegramNotificationSenderTests : SqliteTestBase
{
    private readonly ITelegramBotClient _bot = Substitute.For<ITelegramBotClient>();
    private readonly ILogger<TelegramNotificationSender> _logger = Substitute.For<ILogger<TelegramNotificationSender>>();

    private TelegramNotificationSender CreateSut(string miniAppUrl = "") =>
        new(_bot, Db, Options.Create(new TelegramOptions { MiniAppUrl = miniAppUrl }), _logger);

    [Fact]
    public async Task SendAsync_KnownUser_SendsMessageToTheirTelegramId()
    {
        var user = Db.AddUser();
        var (family, _) = Db.SeedFamilyWithAdmin();
        var notification = TestData.NewNotification(user.Id, family.Id, "dk-1");
        Db.Notifications.Add(notification);
        await Db.SaveChangesAsync();
        _bot.SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>()).Returns(new Message());

        await CreateSut().SendAsync(notification);

        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.ChatId == user.TelegramId && r.Text.Contains(notification.Title)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_UserWithoutTelegramId_LogsWarningAndDoesNotCallBot()
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

        await _bot.DidNotReceive().SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_BotThrows_ExceptionIsSwallowed()
    {
        // Инвариант из докстринга: ошибка одного отправления не должна обрывать
        // ReminderScanJob.SendPendingAsync, идущий по списку оповещений одним циклом.
        var user = Db.AddUser();
        var (family, _) = Db.SeedFamilyWithAdmin();
        var notification = TestData.NewNotification(user.Id, family.Id, "dk-1");
        Db.Notifications.Add(notification);
        await Db.SaveChangesAsync();
        _bot.SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("telegram api down"));

        var act = async () => await CreateSut().SendAsync(notification);

        await act.Should().NotThrowAsync();
    }
}
