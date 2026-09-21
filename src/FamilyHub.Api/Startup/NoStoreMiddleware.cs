namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — запрет кэширования для /api и /internal,
/// без изменения поведения/порядка.
/// </summary>
public static class NoStoreMiddleware
{
    /// <summary>Обнаружен случай (Telegram Mini App WebView), когда GET /api/auth/me иногда
    /// получал закэшированный где-то на клиенте index.html вместо актуального JSON, хотя прямые
    /// HTTP-проверки того же бэкенда/прокси всегда отвечали корректно — сам ответ API не запрещал
    /// явно своё кэширование. Response Cache-Control — авторитетный сигнал для любого кэша
    /// (клиентского, прокси), надёжнее одних только запросных заголовков.</summary>
    public static WebApplication UseNoStoreForApi(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/internal"))
            {
                context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                context.Response.Headers.Pragma = "no-cache";
            }
            await next();
        });

        return app;
    }
}
