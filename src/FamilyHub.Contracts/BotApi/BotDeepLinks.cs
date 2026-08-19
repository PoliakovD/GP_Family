namespace FamilyHub.Contracts.BotApi;

/// <summary>
/// Префиксы аргумента /start Telegram-бота (t.me/bot?start=&lt;префикс&gt;&lt;код&gt;) — общий
/// источник правды между FamilyHub.Api (генерация ссылок в InviteEndpoints/AuthEndpoints) и
/// FamilyHub.TelegramBot (разбор в TelegramUpdateHandler). Раньше жили как консты на самом
/// TelegramUpdateHandler, но после выноса бота в отдельный процесс это уже межпроцессный
/// контракт, а не деталь одного хендлера.
/// </summary>
public static class BotDeepLinks
{
    /// <summary>Инвайт в семью: t.me/bot?start=invite___&lt;hex-код&gt;.</summary>
    public const string InvitePrefix = "invite___";

    /// <summary>Привязка Telegram к веб/email-аккаунту: t.me/bot?start=link___&lt;код&gt;.</summary>
    public const string LinkPrefix = "link___";
}
