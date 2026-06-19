namespace FamilyHub.Infrastructure.Telegram;

public interface ITelegramInitDataValidator
{
    /// <summary>
    /// Валидирует initData Telegram Mini App. Возвращает данные пользователя при успехе,
    /// null — если подпись неверна, поля отсутствуют или auth_date просрочен.
    /// </summary>
    TelegramInitDataResult? Validate(string initData);
}
