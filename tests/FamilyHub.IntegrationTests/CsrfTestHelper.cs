namespace FamilyHub.IntegrationTests;

/// <summary>
/// HttpClient, в отличие от Angular (withXsrfConfiguration), не читает cookie и не подставляет
/// заголовок сам — этот хелпер воспроизводит то же поведение для тестов с автоматическим
/// cookie-jar (обычный <c>factory.CreateClient()</c>).
///
/// Токен ОБЯЗАТЕЛЬНО берётся с GET /api/auth/me (аутентифицированный запрос), а не с ответа
/// login/register/confirm/refresh — те AllowAnonymous, и IAntiforgery.GetAndStoreTokens
/// привязывает выданный токен к identity ТЕКУЩЕГО запроса; в момент этих эндпоинтов новая
/// cookie-сессия ещё не подействовала (запрос всё ещё анонимен для сервера), и такой токен не
/// проходит валидацию на последующих аутентифицированных мутирующих запросах (см.
/// PwaSessionCookieWriter.IssueCsrfCookie). Ровно так же ведёт себя и реальный SPA — он тоже
/// вызывает /me сразу после login/register/reset-password (см. auth.service.ts) и на каждом
/// старте — так что этот хелпер лишь воспроизводит настоящий клиентский поток, а не подстраивается
/// под тест.
/// </summary>
internal static class CsrfTestHelper
{
    private const string CookieName = "XSRF-TOKEN";
    private const string HeaderName = "X-XSRF-TOKEN";

    /// <summary>Вызвать один раз сразу после установки PWA-сессии (login/register/confirm) —
    /// DefaultRequestHeaders переживает все последующие запросы этого клиента.</summary>
    public static async Task CaptureCsrfTokenAsync(HttpClient client)
    {
        var me = await client.GetAsync("/api/auth/me");
        CaptureFromResponse(client, me);
    }

    private static void CaptureFromResponse(HttpClient client, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies)) return;

        foreach (var header in cookies)
        {
            if (!header.StartsWith(CookieName + "=", StringComparison.Ordinal)) continue;

            var value = header[(CookieName.Length + 1)..];
            var end = value.IndexOf(';');
            if (end >= 0) value = value[..end];

            client.DefaultRequestHeaders.Remove(HeaderName);
            client.DefaultRequestHeaders.Add(HeaderName, Uri.UnescapeDataString(value));
            return;
        }
    }
}
