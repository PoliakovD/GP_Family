using FamilyHub.Contracts.BotApi;
using FamilyHub.TelegramBot.Api;
using FamilyHub.TelegramBot.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace FamilyHub.TelegramBot.Webhook;

/// <summary>
/// Telegram-бот как тонкий клиент (этап 4 п.12 брифа, перенесён в отдельный процесс — ADR-0008):
/// только маппинг команд на /internal/bot/* эндпоинты FamilyHub.Api (IFamilyHubApiClient). Никакой
/// бизнес-логики и никакого доступа к БД — она целиком в Api/Features и модулях, как для Mini App
/// и любого другого клиента; резолв личности (telegramId → userId) тоже остаётся на стороне Api.
/// </summary>
public class TelegramUpdateHandler(
    ITelegramBotClient bot,
    IFamilyHubApiClient api,
    IOptions<BotOptions> options,
    ILogger<TelegramUpdateHandler> logger)
{
    private const string LinkCallbackPrefix = "link:";

    private const string HelpText =
        "Команды:\n/start — открыть приложение\n/start <ссылка инвайта> — принять приглашение в семью\n/help — эта справка";

    private const string ApiUnavailableText = "Сервис временно недоступен, попробуйте позже.";

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

        // Ручной ввод кода привязки (без перехода по deep-link) — та же инструкция "введите код
        // вручную" в SettingsProfileComponent. Формат совпадает с TelegramLinkService.StartAsync
        // (Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) — ровно 32 hex-символа);
        // ToLowerInvariant на случай другого регистра при копипасте — хэш сравнивается как есть.
        if (LooksLikeLinkCode(text))
        {
            await HandleLinkStartAsync(message.Chat.Id, text.ToLowerInvariant(), ct);
            return;
        }

        await ReplyWithMiniAppButtonAsync(message.Chat.Id, $"Не понимаю эту команду.\n\n{HelpText}", ct);
    }

    private static bool LooksLikeLinkCode(string text) => text.Length == 32 && text.All(Uri.IsHexDigit);

    private async Task HandleStartAsync(Message message, CancellationToken ct)
    {
        var from = message.From!;

        // "/start <аргумент>" — deep-link (t.me/bot?start=<аргумент>); без аргумента — просто приветствие.
        var parts = message.Text!.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var arg = parts.Length > 1 ? parts[1] : null;

        // Привязка Telegram к веб-аккаунту (LinkPrefix) обрабатывается отдельно, ДО резолва
        // ниже: резолв/создание пользователя для этой ветки — только внутри
        // TelegramLinkService.ConfirmAsync (на стороне Api), после явного подтверждения кнопкой.
        if (arg is not null && arg.StartsWith(BotDeepLinks.LinkPrefix, StringComparison.Ordinal))
        {
            await HandleLinkStartAsync(message.Chat.Id, arg[BotDeepLinks.LinkPrefix.Length..], ct);
            return;
        }

        // Новый формат инвайта: аргумент начинается с InvitePrefix ("invite___<hex>").
        // Старый формат (сырой hex-код) поддерживается для обратной совместимости.
        string? inviteCode = arg is null
            ? null
            : arg.StartsWith(BotDeepLinks.InvitePrefix, StringComparison.Ordinal) ? arg[BotDeepLinks.InvitePrefix.Length..] : arg;

        try
        {
            if (inviteCode is null)
            {
                // Бот НИКОГДА не создаёт пользователя сам (тот же lookup-only принцип, что и в
                // TelegramMiniAppAuthenticationHandler, и в ветке LinkPrefix выше) — "голый"
                // Telegram-аккаунт без email воспроизводил бы именно ту проблему раздельных
                // личностей, ради устранения которой это всё затевалось.
                var resolved = await api.ResolveUserAsync(from.Id, ct);
                var reply = resolved.IsLinked
                    ? $"Добро пожаловать в FamilyHub!\n\n{HelpText}"
                    : $"Добро пожаловать в FamilyHub! Откройте приложение и подтвердите email, чтобы начать.\n\n{HelpText}";
                await ReplyWithMiniAppButtonAsync(message.Chat.Id, reply, ct);
                return;
            }

            // Резолв личности и погашение инвайта теперь одной операцией на стороне Api —
            // BotRedeemOutcome.NotLinked заменяет прежний отдельный lookup ДО редима.
            var result = await api.RedeemInviteAsync(inviteCode, from.Id, ct);
            var redeemReply = result.Outcome switch
            {
                BotRedeemOutcome.NotLinked =>
                    "Чтобы принять приглашение, сначала откройте приложение и подтвердите email — "
                        + "после этого перейдите по ссылке приглашения ещё раз.",
                BotRedeemOutcome.Joined => "Вы успешно присоединились к семье!",
                BotRedeemOutcome.PendingApproval => "Заявка отправлена — ждите одобрения администратора семьи.",
                BotRedeemOutcome.AlreadyMember => "Вы уже состоите в этой семье.",
                BotRedeemOutcome.Revoked => "Этот инвайт отозван.",
                BotRedeemOutcome.Expired => "Этот инвайт просрочен.",
                BotRedeemOutcome.Exhausted => "Этот инвайт уже использован максимальное число раз.",
                BotRedeemOutcome.NotForYou => "Этот инвайт предназначен другому пользователю.",
                BotRedeemOutcome.NotFound => "Инвайт не найден — проверьте ссылку.",
                _ => "Не удалось обработать инвайт.",
            };
            await ReplyWithMiniAppButtonAsync(message.Chat.Id, redeemReply, ct);
        }
        catch (FamilyHubApiUnavailableException ex)
        {
            logger.LogError(ex, "FamilyHub.Api недоступен при обработке /start от {TelegramId}", from.Id);
            await ReplyWithMiniAppButtonAsync(message.Chat.Id, ApiUnavailableText, ct);
        }
    }

    private async Task HandleLinkStartAsync(long chatId, string code, CancellationToken ct)
    {
        PeekLinkResponse peek;
        try
        {
            peek = await api.PeekTelegramLinkAsync(code, ct);
        }
        catch (FamilyHubApiUnavailableException ex)
        {
            logger.LogError(ex, "FamilyHub.Api недоступен при peek кода привязки");
            await bot.SendMessage(chatId, ApiUnavailableText, cancellationToken: ct);
            return;
        }

        if (!peek.Found)
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

        string reply;
        try
        {
            var result = await api.ConfirmTelegramLinkAsync(
                code, from.Id, string.IsNullOrWhiteSpace(displayName) ? null : displayName, from.Username, ct);

            reply = result.Outcome switch
            {
                BotLinkOutcome.Linked => "Готово! Telegram привязан к вашему аккаунту FamilyHub.",
                BotLinkOutcome.Merged =>
                    "Готово! Этот Telegram уже использовался в FamilyHub — данные объединены с вашим аккаунтом.",
                BotLinkOutcome.TelegramAlreadyOnThisAccount => "Этот Telegram уже привязан к данному аккаунту.",
                BotLinkOutcome.InvalidCode => "Код привязки недействителен, истёк или уже использован.",
                _ => "Не удалось выполнить привязку.",
            };
        }
        catch (FamilyHubApiUnavailableException ex)
        {
            logger.LogError(ex, "FamilyHub.Api недоступен при подтверждении привязки");
            reply = ApiUnavailableText;
        }

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
