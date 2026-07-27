namespace FamilyHub.Infrastructure.Auth;

public static class AuthSchemes
{
    public const string TelegramMiniApp = "TelegramMiniApp";

    /// <summary>Cookie-сессия PWA-входа (email + пароль, этап 2 п.2.4).</summary>
    public const string PwaCookie = "PwaCookie";

    /// <summary>Селектор схемы по признакам запроса (tma-заголовок → Telegram, иначе cookie).</summary>
    public const string Smart = "Smart";

    /// <summary>Только для Development — заглушка без бота, авторизация по заголовку X-Dev-TelegramId.</summary>
    public const string Dev = "Dev";
}
