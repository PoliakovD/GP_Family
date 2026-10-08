namespace FamilyHub.Infrastructure.Telegram;

public interface ITelegramInitDataValidator
{
    /// <summary>
    /// Валидирует initData Telegram Mini App. Возвращает данные пользователя при успехе,
    /// null — если подпись неверна, поля отсутствуют или auth_date просрочен.
    /// </summary>
    TelegramInitDataResult? Validate(string initData) => Check(initData).Result;

    /// <summary>То же, что <see cref="Validate"/>, но различает «просрочена» и «недействительна» —
    /// клиенту Mini App нужно сказать «перезапустите приложение», а не уводить на повторную привязку
    /// (аудит security-audit-2026-10, M6).</summary>
    TelegramInitDataCheck Check(string initData);
}

public enum TelegramInitDataFailure
{
    None,

    /// <summary>Подпись, формат или обязательные поля (в том числе auth_date) не прошли проверку.</summary>
    Invalid,

    /// <summary>Подпись верна, но initData старше Telegram:MaxInitDataAge.</summary>
    Expired,
}

public record TelegramInitDataCheck(TelegramInitDataResult? Result, TelegramInitDataFailure Failure)
{
    public static readonly TelegramInitDataCheck Invalid = new(null, TelegramInitDataFailure.Invalid);
    public static readonly TelegramInitDataCheck Expired = new(null, TelegramInitDataFailure.Expired);
}
