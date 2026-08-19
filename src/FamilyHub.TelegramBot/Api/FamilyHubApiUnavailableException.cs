namespace FamilyHub.TelegramBot.Api;

/// <summary>
/// Api не ответил (не-2xx, таймаут, сеть недоступна) на вызов /internal/bot/*. Хендлер ловит
/// это исключение и отвечает пользователю "сервис временно недоступен" вместо падения — Telegram
/// и так ретраит доставку вебхука при ошибке, отдельный Polly-стек не нужен.
/// </summary>
public class FamilyHubApiUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
