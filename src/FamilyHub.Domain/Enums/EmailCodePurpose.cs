namespace FamilyHub.Domain.Enums;

/// <summary>Назначение одноразового email-кода (этап 2 п.2.4).</summary>
public enum EmailCodePurpose
{
    /// <summary>Регистрация нового PWA-аккаунта.</summary>
    Register = 0,

    /// <summary>Привязка email к существующему (Telegram) аккаунту.</summary>
    LinkEmail = 1,

    /// <summary>Сброс забытого пароля существующего PWA-аккаунта.</summary>
    ResetPassword = 2,

    /// <summary>
    /// Привязка Telegram Mini App к email-аккаунту (первый вход, анонимный поток —
    /// не путать с LinkEmail, где привязка идёт от уже аутентифицированного пользователя).
    /// UserId неизвестен на момент выпуска кода — определяется в момент подтверждения по email.
    /// </summary>
    TelegramBind = 3,
}
