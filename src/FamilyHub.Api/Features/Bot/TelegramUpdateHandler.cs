using FamilyHub.Api.Features.Auth;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.CurrentUser;
using FamilyHub.Infrastructure.Telegram;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace FamilyHub.Api.Features.Bot;

/// <summary>
/// Telegram-бот как тонкий клиент (этап 4 п.12 брифа): только маппинг команд на уже
/// существующие сервисы (InviteService, IUserProvisioningService, TelegramLinkService).
/// Никакой бизнес-логики — она целиком в Api/Features и модулях, как для Mini App и любого
/// другого клиента.
/// </summary>
public class TelegramUpdateHandler(
    ITelegramBotClient bot,
    IUserProvisioningService userProvisioning,
    InviteService invites,
    TelegramLinkService links,
    IOptions<TelegramOptions> options)
{
    /// <summary>
    /// Префикс аргумента /start для инвайтов: t.me/bot?start=invite___&lt;hex-код&gt;.
    /// Используется и в InviteEndpoints при генерации ссылки — единый источник истины.
    /// </summary>
    public const string InvitePrefix = "invite___";

    /// <summary>
    /// Префикс аргумента /start для привязки Telegram к веб/email-аккаунту:
    /// t.me/bot?start=link___&lt;код&gt;. Используется и в AuthEndpoints при генерации
    /// ссылки — единый источник истины.
    /// </summary>
    public const string LinkPrefix = "link___";

    private const string LinkCallbackPrefix = "link:";

    private const string HelpText =
        "Команды:\n/start — открыть приложение\n/start <ссылка инвайта> — принять приглашение в семью\n/help — эта справка";

    public async Task HandleAsync(Update update, CancellationToken ct)
    {
        if (update.CallbackQuery is { } callbackQuery)
        {
            await HandleCallbackQueryAsync(callbackQuery, ct);
            return;
        }

        var message = update.Message;
        if (message?.Text is null || message.From is null)
            return; // тонкий клиент реагирует только на текстовые сообщения от пользователя (+ callback выше)

        var text = message.Text.Trim();

        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            await HandleStartAsync(message, ct);
            return;
        }

        if (text.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
        {
            await ReplyWithMiniAppButtonAsync(message.Chat.Id, HelpText, ct);
            return;
        }

        await ReplyWithMiniAppButtonAsync(message.Chat.Id, $"Не понимаю эту команду.\n\n{HelpText}", ct);
    }

    private async Task HandleStartAsync(Message message, CancellationToken ct)
    {
        var from = message.From!;

        // "/start <аргумент>" — deep-link (t.me/bot?start=<аргумент>); без аргумента — просто приветствие.
        var parts = message.Text!.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var arg = parts.Length > 1 ? parts[1] : null;

        // Привязка Telegram к веб-аккаунту (LinkPrefix) — намеренно ДО провижининга ниже:
        // GetOrCreateUserIdAsync тут же создал бы Telegram-only пользователя, и любая
        // привязка автоматически стала бы merge'ем, даже когда этот Telegram впервые видит
        // FamilyHub. Резолв/создание пользователя для этой ветки — только внутри
        // TelegramLinkService.ConfirmAsync, после явного подтверждения кнопкой.
        if (arg is not null && arg.StartsWith(LinkPrefix, StringComparison.Ordinal))
        {
            await HandleLinkStartAsync(message.Chat.Id, arg[LinkPrefix.Length..], ct);
            return;
        }

        var displayName = string.Join(' ', new[] { from.FirstName, from.LastName }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        var userId = await userProvisioning.GetOrCreateUserIdAsync(
            from.Id, string.IsNullOrWhiteSpace(displayName) ? null : displayName, from.Username, ct);

        // Новый формат инвайта: аргумент начинается с InvitePrefix ("invite___<hex>").
        // Старый формат (сырой hex-код) поддерживается для обратной совместимости.
        string? inviteCode = arg is null
            ? null
            : arg.StartsWith(InvitePrefix, StringComparison.Ordinal) ? arg[InvitePrefix.Length..] : arg;

        if (inviteCode is null)
        {
            await ReplyWithMiniAppButtonAsync(message.Chat.Id, $"Добро пожаловать в FamilyHub!\n\n{HelpText}", ct);
            return;
        }

        var result = await invites.RedeemInviteAsync(inviteCode, userId, ct);
        var reply = result switch
        {
            RedeemResult.Joined => "Вы успешно присоединились к семье!",
            RedeemResult.PendingApproval => "Заявка отправлена — ждите одобрения администратора семьи.",
            RedeemResult.AlreadyMember => "Вы уже состоите в этой семье.",
            RedeemResult.Revoked => "Этот инвайт отозван.",
            RedeemResult.Expired => "Этот инвайт просрочен.",
            RedeemResult.Exhausted => "Этот инвайт уже использован максимальное число раз.",
            RedeemResult.NotForYou => "Этот инвайт предназначен другому пользователю.",
            RedeemResult.NotFound => "Инвайт не найден — проверьте ссылку.",
            _ => "Не удалось обработать инвайт.",
        };

        await ReplyWithMiniAppButtonAsync(message.Chat.Id, reply, ct);
    }

    private async Task HandleLinkStartAsync(long chatId, string code, CancellationToken ct)
    {
        var peek = await links.PeekAsync(code, ct);
        if (peek is null)
        {
            await bot.SendMessage(
                chatId,
                "Код привязки недействителен или истёк. Запросите новый в настройках FamilyHub.",
                cancellationToken: ct);
            return;
        }

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("Привязать", $"{LinkCallbackPrefix}{code}") },
            new[] { InlineKeyboardButton.WithCallbackData("Отмена", "link-cancel") },
        });

        await bot.SendMessage(
            chatId,
            $"Привязать этот Telegram-аккаунт к {peek.MaskedEmail}?",
            replyMarkup: keyboard,
            cancellationToken: ct);
    }

    private async Task HandleCallbackQueryAsync(CallbackQuery callbackQuery, CancellationToken ct)
    {
        var chatId = callbackQuery.Message?.Chat.Id;
        var messageId = callbackQuery.Message?.MessageId;
        if (chatId is null || messageId is null || callbackQuery.Data is null)
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        if (callbackQuery.Data == "link-cancel")
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            await bot.EditMessageText(chatId.Value, messageId.Value, "Привязка отменена.", cancellationToken: ct);
            return;
        }

        if (!callbackQuery.Data.StartsWith(LinkCallbackPrefix, StringComparison.Ordinal))
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        var code = callbackQuery.Data[LinkCallbackPrefix.Length..];
        var from = callbackQuery.From;
        var displayName = string.Join(' ', new[] { from.FirstName, from.LastName }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        var result = await links.ConfirmAsync(
            code, from.Id, string.IsNullOrWhiteSpace(displayName) ? null : displayName, from.Username, ct);

        var reply = result switch
        {
            LinkTelegramResult.Linked => "Готово! Telegram привязан к вашему аккаунту FamilyHub.",
            LinkTelegramResult.Merged =>
                "Готово! Этот Telegram уже использовался в FamilyHub — данные объединены с вашим аккаунтом.",
            LinkTelegramResult.TelegramAlreadyOnThisAccount => "Этот Telegram уже привязан к данному аккаунту.",
            LinkTelegramResult.InvalidCode => "Код привязки недействителен, истёк или уже использован.",
            _ => "Не удалось выполнить привязку.",
        };

        await bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);
        await bot.EditMessageText(chatId.Value, messageId.Value, reply, cancellationToken: ct);
    }

    private async Task ReplyWithMiniAppButtonAsync(long chatId, string text, CancellationToken ct)
    {
        var miniAppUrl = options.Value.MiniAppUrl;
        ReplyMarkup? markup = string.IsNullOrWhiteSpace(miniAppUrl)
            ? null
            : new InlineKeyboardMarkup(InlineKeyboardButton.WithWebApp("Открыть FamilyHub", new WebAppInfo(miniAppUrl)));

        await bot.SendMessage(chatId, text, replyMarkup: markup, cancellationToken: ct);
    }
}
