using FamilyHub.Api.Features.Bot;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.CurrentUser;
using FamilyHub.Infrastructure.Telegram;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Xunit;

namespace FamilyHub.UnitTests.Features.Bot;

public class TelegramUpdateHandlerTests : SqliteTestBase
{
    private readonly ITelegramBotClient _bot = Substitute.For<ITelegramBotClient>();
    private readonly TelegramUpdateHandler _sut;

    public TelegramUpdateHandlerTests()
    {
        _bot.SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>()).Returns(new Message());
        var access = new FamilyAccessService(Db);
        IUserProvisioningService provisioning = new UserProvisioningService(Db);
        var invites = new InviteService(Db, access);
        _sut = new TelegramUpdateHandler(_bot, provisioning, invites, Options.Create(new TelegramOptions()));
    }

    private static Update StartUpdate(long fromId, long chatId, string? argument, string firstName = "Ada")
    {
        var text = argument is null ? "/start" : $"/start {argument}";
        return new Update
        {
            Message = new Message
            {
                Text = text,
                Chat = new Chat { Id = chatId },
                From = new User { Id = fromId, FirstName = firstName },
            },
        };
    }

    [Fact]
    public async Task HandleAsync_StartWithoutCode_RepliesWithWelcome_AndProvisionsUser()
    {
        await _sut.HandleAsync(StartUpdate(fromId: 111, chatId: 111, argument: null), CancellationToken.None);

        Db.Users.Should().ContainSingle(u => u.TelegramId == 111);
        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.ChatId == 111 && r.Text.Contains("Добро пожаловать")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_StartWithValidLinkInviteCode_ResultsInPendingApprovalAndReplies()
    {
        // Через deep-link бота ("/start <код>") код всегда ссылочный (TargetUserId неизвестен на
        // момент создания инвайта) — редимится через InviteService.RedeemInviteAsync как обычно.
        var (family, admin) = Db.SeedFamilyWithAdmin();
        const long targetTelegramId = 222;
        var invite = TestData.NewInvite(family.Id, admin.Id, maxUses: 5);
        Db.FamilyInvites.Add(invite);
        await Db.SaveChangesAsync();

        await _sut.HandleAsync(StartUpdate(targetTelegramId, targetTelegramId, invite.Code), CancellationToken.None);

        var user = Db.Users.Single(u => u.TelegramId == targetTelegramId);
        Db.FamilyMembers.Should().ContainSingle(m => m.FamilyId == family.Id && m.UserId == user.Id && m.Status == MemberStatus.PendingApproval);
        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.Text.Contains("Заявка отправлена")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_StartWithUnknownCode_RepliesNotFound()
    {
        await _sut.HandleAsync(StartUpdate(333, 333, "no-such-code"), CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.Text.Contains("не найден")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_Help_RepliesWithCommandList()
    {
        var update = new Update
        {
            Message = new Message { Text = "/help", Chat = new Chat { Id = 444 }, From = new User { Id = 444, FirstName = "Bob" } },
        };

        await _sut.HandleAsync(update, CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.Text.Contains("Команды")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_UnknownCommand_RepliesWithFallbackHelp()
    {
        var update = new Update
        {
            Message = new Message { Text = "/unknown", Chat = new Chat { Id = 555 }, From = new User { Id = 555, FirstName = "X" } },
        };

        await _sut.HandleAsync(update, CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.Text.Contains("Не понимаю")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NonTextUpdate_DoesNothing()
    {
        var update = new Update { Message = new Message { Chat = new Chat { Id = 666 }, From = new User { Id = 666 } } };

        await _sut.HandleAsync(update, CancellationToken.None);

        await _bot.DidNotReceive().SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>());
    }
}
