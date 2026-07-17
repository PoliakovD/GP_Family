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
/// существующие сервисы (InviteService, IUserProvisioningService). Никакой бизнес-логики —
/// она целиком в Api/Features и модулях, как для Mini App и любого другого клиента.
/// </summary>
public class TelegramUpdateHandler(
    ITelegramBotClient bot,
    IUserProvisioningService userProvisioning,
    InviteService invites,
    IOptions<TelegramOptions> options)
{
    /// <summary>
    /// Префикс аргумента /start для инвайтов: t.me/bot?start=invite___&lt;hex-код&gt;.
    /// Используется и в InviteEndpoints при генерации ссылки — единый источник истины.
    /// </summary>
    public const string InvitePrefix = "invite___";

    private const string HelpText =
        "Команды:\n/start — открыть приложение\n/start <ссылка инвайта> — принять приглашение в семью\n/help — эта справка";

    public async Task HandleAsync(Update update, CancellationToken ct)
    {
        var message = update.Message;
        if (message?.Text is null || message.From is null)
            return; // тонкий клиент реагирует только на текстовые сообщения от пользователя

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
        var displayName = string.Join(' ', new[] { from.FirstName, from.LastName }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        var userId = await userProvisioning.GetOrCreateUserIdAsync(
            from.Id, string.IsNullOrWhiteSpace(displayName) ? null : displayName, from.Username, ct);

        // "/start <аргумент>" — deep-link (t.me/bot?start=<аргумент>); без аргумента — просто приветствие.
        // Новый формат: аргумент начинается с InvitePrefix ("invite___<hex>").
        // Старый формат (сырой hex-код) поддерживается для обратной совместимости.
        var parts = message.Text!.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string? inviteCode = null;
        if (parts.Length > 1)
        {
            var arg = parts[1];
            inviteCode = arg.StartsWith(InvitePrefix, StringComparison.Ordinal)
                ? arg[InvitePrefix.Length..]
                : arg; // обратная совместимость: сырой код без префикса
        }

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

    private async Task ReplyWithMiniAppButtonAsync(long chatId, string text, CancellationToken ct)
    {
        var miniAppUrl = options.Value.MiniAppUrl;
        ReplyMarkup? markup = string.IsNullOrWhiteSpace(miniAppUrl)
            ? null
            : new InlineKeyboardMarkup(InlineKeyboardButton.WithWebApp("Открыть FamilyHub", new WebAppInfo(miniAppUrl)));

        await bot.SendMessage(chatId, text, replyMarkup: markup, cancellationToken: ct);
    }
}
