using FamilyHub.Api.Features.Account;
using FamilyHub.Api.Features.Auth;
using FamilyHub.Api.Features.Bot;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.CurrentUser;
using FamilyHub.Infrastructure.Telegram;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly TelegramLinkService _links;

    public TelegramUpdateHandlerTests()
    {
        _bot.SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>()).Returns(new Message());
        var access = new FamilyAccessService(Db, NullLogger<FamilyAccessService>.Instance);
        IUserProvisioningService provisioning = new UserProvisioningService(Db, NullLogger<UserProvisioningService>.Instance);
        var invites = new InviteService(
            Db, access, new TestSupport.RecordingDomainEventPublisher(), NullLogger<InviteService>.Instance);
        var merge = new AccountMergeService(Db, NullLogger<AccountMergeService>.Instance);
        _links = new TelegramLinkService(Db, merge, NullLogger<TelegramLinkService>.Instance);
        _sut = new TelegramUpdateHandler(_bot, provisioning, invites, _links, Options.Create(new TelegramOptions()));
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
    public async Task HandleAsync_StartWithoutCode_UnboundTelegramId_RepliesWithWelcome_DoesNotProvision()
    {
        // Ровно тот же lookup-only принцип, что и в TelegramMiniAppAuthenticationHandler:
        // бот не должен создавать "голого" Telegram-only пользователя без email — это
        // именно то, что раньше требовало последующего слияния аккаунтов.
        await _sut.HandleAsync(StartUpdate(fromId: 111, chatId: 111, argument: null), CancellationToken.None);

        Db.Users.Should().NotContain(u => u.TelegramId == 111);
        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.ChatId == 111 && r.Text.Contains("Добро пожаловать")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_StartWithInviteCode_UnboundTelegramId_DoesNotProvision_AsksToBindFirst()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        const long targetTelegramId = 222;
        var invite = TestData.NewInvite(family.Id, admin.Id, maxUses: 5);
        Db.FamilyInvites.Add(invite);
        await Db.SaveChangesAsync();

        await _sut.HandleAsync(StartUpdate(targetTelegramId, targetTelegramId, invite.Code), CancellationToken.None);

        Db.Users.Should().NotContain(u => u.TelegramId == targetTelegramId);
        Db.FamilyMembers.Should().HaveCount(1, "инвайт не должен редимиться до привязки email — в семье остаётся только сидированный admin");
        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.Text.Contains("сначала откройте приложение")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_StartWithValidLinkInviteCode_AlreadyBoundTelegramId_ResultsInPendingApprovalAndReplies()
    {
        // Через deep-link бота ("/start <код>") код всегда ссылочный (TargetUserId неизвестен на
        // момент создания инвайта) — редимится через InviteService.RedeemInviteAsync как обычно,
        // но только для УЖЕ привязанного (email-anchor) аккаунта — см. lookup-only выше.
        var (family, admin) = Db.SeedFamilyWithAdmin();
        const long targetTelegramId = 222;
        var boundUser = AddWebUser("bound-invitee@example.com");
        boundUser.TelegramId = targetTelegramId;
        await Db.SaveChangesAsync();
        var invite = TestData.NewInvite(family.Id, admin.Id, maxUses: 5);
        Db.FamilyInvites.Add(invite);
        await Db.SaveChangesAsync();

        await _sut.HandleAsync(StartUpdate(targetTelegramId, targetTelegramId, invite.Code), CancellationToken.None);

        Db.FamilyMembers.Should().ContainSingle(m => m.FamilyId == family.Id && m.UserId == boundUser.Id && m.Status == MemberStatus.PendingApproval);
        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.Text.Contains("Заявка отправлена")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_StartWithUnknownCode_UnboundTelegramId_AsksToBindFirst()
    {
        await _sut.HandleAsync(StartUpdate(333, 333, "no-such-code"), CancellationToken.None);

        // Неизвестный TelegramId никогда не резолвится в userId, поэтому редимить нечего — даже
        // до проверки самого инвайт-кода отвечаем предложением сначала привязать email.
        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.Text.Contains("сначала откройте приложение")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_StartWithUnknownCode_AlreadyBoundTelegramId_RepliesNotFound()
    {
        const long telegramId = 334;
        var boundUser = AddWebUser("bound-unknown-code@example.com");
        boundUser.TelegramId = telegramId;
        await Db.SaveChangesAsync();

        await _sut.HandleAsync(StartUpdate(telegramId, telegramId, "no-such-code"), CancellationToken.None);

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

    private Domain.Entities.User AddWebUser(string email = "danil@example.com")
    {
        var user = new Domain.Entities.User
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = "hash",
            DisplayName = "Web User",
            CreatedAt = DateTime.UtcNow,
        };
        Db.Users.Add(user);
        Db.SaveChanges();
        return user;
    }

    [Fact]
    public async Task HandleAsync_StartWithLinkCode_ShowsConfirmKeyboard_AndDoesNotProvisionTelegramUser()
    {
        var webUser = AddWebUser();
        var (_, code, _) = await _links.StartAsync(webUser.Id);

        await _sut.HandleAsync(
            StartUpdate(777, 777, $"{TelegramUpdateHandler.LinkPrefix}{code}"), CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.ChatId == 777 && r.Text.Contains("d***@example.com")
                && r.ReplyMarkup is Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup),
            Arg.Any<CancellationToken>());
        // Ветка привязки не должна резолвить/создавать пользователя для этого TelegramId —
        // иначе КАЖДАЯ привязка стала бы merge'ем, даже для впервые увиденного Telegram.
        Db.Users.Should().NotContain(u => u.TelegramId == 777);
        Db.Users.Should().ContainSingle(); // только исходный web-пользователь
    }

    [Fact]
    public async Task HandleAsync_StartWithInvalidLinkCode_RepliesWithError()
    {
        await _sut.HandleAsync(
            StartUpdate(778, 778, $"{TelegramUpdateHandler.LinkPrefix}bogus-code"), CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.Text.Contains("недействителен")),
            Arg.Any<CancellationToken>());
    }

    private static Update TextUpdate(long fromId, long chatId, string text, string firstName = "Ada") => new()
    {
        Message = new Message
        {
            Text = text,
            Chat = new Chat { Id = chatId },
            From = new User { Id = fromId, FirstName = firstName },
        },
    };

    [Fact]
    public async Task HandleAsync_PlainTextLinkCode_ShowsConfirmKeyboard()
    {
        // Инструкция "введите код вручную" в SettingsProfileComponent — код присылают без
        // /start и без deep-link-префикса, голым текстом сообщения.
        var webUser = AddWebUser();
        var (_, code, _) = await _links.StartAsync(webUser.Id);

        await _sut.HandleAsync(TextUpdate(782, 782, code), CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.ChatId == 782 && r.Text.Contains("d***@example.com")
                && r.ReplyMarkup is Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup),
            Arg.Any<CancellationToken>());
        Db.Users.Should().NotContain(u => u.TelegramId == 782);
    }

    [Fact]
    public async Task HandleAsync_PlainTextLinkCode_UppercasePastedCode_StillMatches()
    {
        var webUser = AddWebUser();
        var (_, code, _) = await _links.StartAsync(webUser.Id);

        await _sut.HandleAsync(TextUpdate(783, 783, code.ToUpperInvariant()), CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.ChatId == 783 && r.Text.Contains("d***@example.com")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_PlainTextNotLinkCode_FallsBackToUnknownCommand()
    {
        // Защита от слишком широкого совпадения: обычный текст не должен трактоваться как код.
        await _sut.HandleAsync(TextUpdate(784, 784, "привет"), CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.Text.Contains("Не понимаю эту команду")),
            Arg.Any<CancellationToken>());
    }

    private static Update CallbackUpdate(long fromId, long chatId, int messageId, string data, string firstName = "Ada") => new()
    {
        CallbackQuery = new CallbackQuery
        {
            Id = "cb-1",
            From = new User { Id = fromId, FirstName = firstName },
            Message = new Message { Id = messageId, Chat = new Chat { Id = chatId } },
            Data = data,
        },
    };

    [Fact]
    public async Task HandleAsync_CallbackConfirmLink_NoExistingTelegramUser_LinksDirectly()
    {
        var webUser = AddWebUser();
        var (_, code, _) = await _links.StartAsync(webUser.Id);

        await _sut.HandleAsync(CallbackUpdate(779, 779, 42, $"link:{code}"), CancellationToken.None);

        Db.Users.Single(u => u.Id == webUser.Id).TelegramId.Should().Be(779);
        await _bot.Received(1).SendRequest(Arg.Any<AnswerCallbackQueryRequest>(), Arg.Any<CancellationToken>());
        await _bot.Received(1).SendRequest(
            Arg.Is<EditMessageTextRequest>(r => r.ChatId == 779 && r.MessageId == 42 && r.Text.Contains("привязан")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_CallbackConfirmLink_ExistingTelegramUser_MergesAndReplies()
    {
        var webUser = AddWebUser();
        var (_, code, _) = await _links.StartAsync(webUser.Id);
        var telegramUser = new Domain.Entities.User
        {
            Id = Guid.NewGuid(),
            TelegramId = 780,
            DisplayName = "Telegram Only",
            CreatedAt = DateTime.UtcNow,
        };
        Db.Users.Add(telegramUser);
        await Db.SaveChangesAsync();

        await _sut.HandleAsync(CallbackUpdate(780, 780, 43, $"link:{code}"), CancellationToken.None);

        Db.Users.Should().NotContain(u => u.Id == telegramUser.Id);
        Db.Users.Single(u => u.Id == webUser.Id).TelegramId.Should().Be(780);
        await _bot.Received(1).SendRequest(
            Arg.Is<EditMessageTextRequest>(r => r.Text.Contains("объединены")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_CallbackCancel_RepliesCancelled_AndDoesNotLink()
    {
        var webUser = AddWebUser();
        var (_, code, _) = await _links.StartAsync(webUser.Id);

        await _sut.HandleAsync(CallbackUpdate(781, 781, 44, "link-cancel"), CancellationToken.None);

        Db.Users.Single(u => u.Id == webUser.Id).TelegramId.Should().BeNull();
        await _bot.Received(1).SendRequest(
            Arg.Is<EditMessageTextRequest>(r => r.Text.Contains("отменена")),
            Arg.Any<CancellationToken>());
        // Код остаётся годным несмотря на отмену первого показа — не был потреблён.
        (await _links.ConfirmAsync(code!, 781, "X", null)).Should().Be(LinkTelegramResult.Linked);
    }
}
