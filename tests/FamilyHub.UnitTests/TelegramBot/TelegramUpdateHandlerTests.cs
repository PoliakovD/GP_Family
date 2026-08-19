using FamilyHub.Contracts.BotApi;
using FamilyHub.TelegramBot.Api;
using FamilyHub.TelegramBot.Configuration;
using FamilyHub.TelegramBot.Webhook;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Xunit;

namespace FamilyHub.UnitTests.TelegramBot;

/// <summary>
/// После выноса бота в отдельный процесс (ADR-0008) SUT больше не трогает БД напрямую — все
/// прежние сценарии (SqliteTestBase + реальные InviteService/TelegramLinkService/
/// IUserProvisioningService) переписаны на мок IFamilyHubApiClient: ровно то, что раньше делал
/// вызов сервиса, теперь делает HTTP-запрос к /internal/bot/*, и с точки зрения хендлера это
/// одна и та же граница ответственности — просто теперь она сетевая, а не in-process.
/// </summary>
public class TelegramUpdateHandlerTests
{
    private readonly ITelegramBotClient _bot = Substitute.For<ITelegramBotClient>();
    private readonly IFamilyHubApiClient _api = Substitute.For<IFamilyHubApiClient>();
    private readonly global::FamilyHub.TelegramBot.Webhook.TelegramUpdateHandler _sut;

    public TelegramUpdateHandlerTests()
    {
        _bot.SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>()).Returns(new Message());
        _sut = new global::FamilyHub.TelegramBot.Webhook.TelegramUpdateHandler(
            _bot, _api, Options.Create(new BotOptions()), NullLogger<global::FamilyHub.TelegramBot.Webhook.TelegramUpdateHandler>.Instance);
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
    public async Task HandleAsync_StartWithoutCode_UnboundTelegramId_RepliesWithWelcome()
    {
        _api.ResolveUserAsync(111, Arg.Any<CancellationToken>()).Returns(new ResolveUserResponse(IsLinked: false));

        await _sut.HandleAsync(StartUpdate(fromId: 111, chatId: 111, argument: null), CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.ChatId == 111 && r.Text.Contains("Добро пожаловать")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_StartWithoutCode_BoundTelegramId_RepliesWithPlainWelcome()
    {
        _api.ResolveUserAsync(112, Arg.Any<CancellationToken>()).Returns(new ResolveUserResponse(IsLinked: true));

        await _sut.HandleAsync(StartUpdate(fromId: 112, chatId: 112, argument: null), CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.ChatId == 112 && r.Text.Contains("Добро пожаловать")
                && !r.Text.Contains("подтвердите email")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_StartWithInviteCode_NotLinked_AsksToBindFirst()
    {
        _api.RedeemInviteAsync("abc", 222, Arg.Any<CancellationToken>())
            .Returns(new RedeemInviteResponse(BotRedeemOutcome.NotLinked));

        await _sut.HandleAsync(StartUpdate(222, 222, "abc"), CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.Text.Contains("сначала откройте приложение")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_StartWithValidInviteCode_Linked_ResultsInPendingApprovalAndReplies()
    {
        _api.RedeemInviteAsync("abc", 223, Arg.Any<CancellationToken>())
            .Returns(new RedeemInviteResponse(BotRedeemOutcome.PendingApproval));

        await _sut.HandleAsync(StartUpdate(223, 223, "abc"), CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.Text.Contains("Заявка отправлена")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_StartWithInviteCode_Joined_RepliesSuccess()
    {
        _api.RedeemInviteAsync("abc", 224, Arg.Any<CancellationToken>())
            .Returns(new RedeemInviteResponse(BotRedeemOutcome.Joined));

        await _sut.HandleAsync(StartUpdate(224, 224, "abc"), CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.Text.Contains("успешно присоединились")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_StartWithUnknownCode_RepliesNotFound()
    {
        _api.RedeemInviteAsync("no-such-code", 334, Arg.Any<CancellationToken>())
            .Returns(new RedeemInviteResponse(BotRedeemOutcome.NotFound));

        await _sut.HandleAsync(StartUpdate(334, 334, "no-such-code"), CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.Text.Contains("не найден")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_StartWithInviteCode_ApiUnavailable_RepliesWithFallback()
    {
        _api.RedeemInviteAsync("abc", 225, Arg.Any<CancellationToken>())
            .Returns<Task<RedeemInviteResponse>>(_ => throw new FamilyHubApiUnavailableException("boom"));

        await _sut.HandleAsync(StartUpdate(225, 225, "abc"), CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.Text.Contains("временно недоступен")),
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
    public async Task HandleAsync_StartWithLinkCode_ShowsConfirmKeyboard()
    {
        _api.PeekTelegramLinkAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Arg.Any<CancellationToken>())
            .Returns(new PeekLinkResponse(Found: true, "d***@example.com"));

        await _sut.HandleAsync(
            StartUpdate(777, 777, $"{BotDeepLinks.LinkPrefix}aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.ChatId == 777 && r.Text.Contains("d***@example.com")
                && r.ReplyMarkup is Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_StartWithInvalidLinkCode_RepliesWithError()
    {
        _api.PeekTelegramLinkAsync("bogus-code", Arg.Any<CancellationToken>())
            .Returns(new PeekLinkResponse(Found: false, null));

        await _sut.HandleAsync(
            StartUpdate(778, 778, $"{BotDeepLinks.LinkPrefix}bogus-code"), CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.Text.Contains("недействителен")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_PlainTextLinkCode_ShowsConfirmKeyboard()
    {
        // Инструкция "введите код вручную" в SettingsProfileComponent — код присылают без
        // /start и без deep-link-префикса, голым текстом сообщения. Формат — ровно 32 hex-символа
        // (Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) на стороне Api).
        const string code = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        _api.PeekTelegramLinkAsync(code, Arg.Any<CancellationToken>())
            .Returns(new PeekLinkResponse(Found: true, "d***@example.com"));

        await _sut.HandleAsync(TextUpdate(782, 782, code), CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.ChatId == 782 && r.Text.Contains("d***@example.com")
                && r.ReplyMarkup is Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_PlainTextLinkCode_UppercasePastedCode_StillLowercased()
    {
        const string code = "cccccccccccccccccccccccccccccccc";
        _api.PeekTelegramLinkAsync(code, Arg.Any<CancellationToken>())
            .Returns(new PeekLinkResponse(Found: true, "d***@example.com"));

        await _sut.HandleAsync(TextUpdate(783, 783, code.ToUpperInvariant()), CancellationToken.None);

        await _api.Received(1).PeekTelegramLinkAsync(code, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_PlainTextNotLinkCode_FallsBackToUnknownCommand()
    {
        // Защита от слишком широкого совпадения: обычный текст не должен трактоваться как код.
        await _sut.HandleAsync(TextUpdate(784, 784, "привет"), CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.Text.Contains("Не понимаю эту команду")),
            Arg.Any<CancellationToken>());
        await _api.DidNotReceiveWithAnyArgs().PeekTelegramLinkAsync(default!, default);
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
    public async Task HandleAsync_CallbackConfirmLink_Linked_RepliesLinked()
    {
        _api.ConfirmTelegramLinkAsync("code1", 779, "Ada", null, Arg.Any<CancellationToken>())
            .Returns(new ConfirmLinkResponse(BotLinkOutcome.Linked));

        await _sut.HandleAsync(CallbackUpdate(779, 779, 42, "link:code1"), CancellationToken.None);

        await _bot.Received(1).SendRequest(Arg.Any<AnswerCallbackQueryRequest>(), Arg.Any<CancellationToken>());
        await _bot.Received(1).SendRequest(
            Arg.Is<EditMessageTextRequest>(r => r.ChatId == 779 && r.MessageId == 42 && r.Text.Contains("привязан")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_CallbackConfirmLink_Merged_RepliesMerged()
    {
        _api.ConfirmTelegramLinkAsync("code2", 780, "Ada", null, Arg.Any<CancellationToken>())
            .Returns(new ConfirmLinkResponse(BotLinkOutcome.Merged));

        await _sut.HandleAsync(CallbackUpdate(780, 780, 43, "link:code2"), CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<EditMessageTextRequest>(r => r.Text.Contains("объединены")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_CallbackCancel_RepliesCancelled_DoesNotCallApi()
    {
        await _sut.HandleAsync(CallbackUpdate(781, 781, 44, "link-cancel"), CancellationToken.None);

        await _bot.Received(1).SendRequest(
            Arg.Is<EditMessageTextRequest>(r => r.Text.Contains("отменена")),
            Arg.Any<CancellationToken>());
        await _api.DidNotReceiveWithAnyArgs().ConfirmTelegramLinkAsync(default!, default, default, default, default);
    }
}
