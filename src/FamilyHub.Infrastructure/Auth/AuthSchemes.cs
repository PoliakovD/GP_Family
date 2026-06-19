namespace FamilyHub.Infrastructure.Auth;

public static class AuthSchemes
{
    public const string TelegramMiniApp = "TelegramMiniApp";

    /// <summary>Только для Development — заглушка без бота, авторизация по заголовку X-Dev-TelegramId.</summary>
    public const string Dev = "Dev";
}
