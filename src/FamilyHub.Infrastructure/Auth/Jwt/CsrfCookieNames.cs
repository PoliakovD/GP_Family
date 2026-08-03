namespace FamilyHub.Infrastructure.Auth.Jwt;

/// <summary>
/// Публичная (НЕ httpOnly) cookie для CSRF-защиты PWA-сессии — double-submit поверх
/// SameSite=Lax (см. аудит docs/security/module-review-2026-08-02/01-auth-identity.md,
/// находка 4). Значение — IAntiforgery.RequestToken (см. PwaSessionCookieWriter.IssueCsrfCookie);
/// Angular (withXsrfConfiguration в app.config.ts) сам читает эту cookie через document.cookie
/// и подставляет её значение в заголовок X-XSRF-TOKEN на каждый мутирующий запрос — без кода в
/// компонентах/сервисах. Выставляется ИСКЛЮЧИТЕЛЬНО вместе с PWA-сессией (не для Telegram/Dev —
/// там ambient-cookie аутентификации нет, CSRF неприменим по конструкции); CSRF-гейт в
/// Program.cs триггерится ровно наличием этой cookie в запросе.
/// </summary>
public static class CsrfCookieNames
{
    /// <summary>Имя cookie синхронизировано с дефолтом Angular HttpClientXsrfModule.</summary>
    public const string PublicToken = "XSRF-TOKEN";
}
