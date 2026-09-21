using FamilyHub.Infrastructure.Auth;
using FamilyHub.Infrastructure.Auth.Jwt;
using Microsoft.AspNetCore.Antiforgery;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — CSRF-гейт (аудит
/// module-review-2026-08-02/01-auth-identity.md, находка 4), без изменения поведения/порядка.
/// </summary>
public static class CsrfGateMiddleware
{
    /// <summary>Мутирующий /api-запрос, несущий публичную cookie CsrfCookieNames.PublicToken
    /// (выставляется ТОЛЬКО вместе с PWA-сессией, см. PwaSessionCookieWriter.IssueCsrfCookie),
    /// обязан нести валидный заголовок X-XSRF-TOKEN. Telegram/Dev-запросы эту cookie никогда не
    /// получают — пропускаются естественно, без отдельной проверки auth-схемы. IsRequestValidAsync
    /// при наличии заголовка читает токен ИЗ заголовка, не трогая тело запроса — безопасно и для
    /// multipart-загрузок.
    ///
    /// Отладка 2026-08-20 (после включения персистентных ключей Data Protection): гейт
    /// срабатывал и на АНОНИМНЫХ запросах (/api/auth/login и т.п.), если в браузере оставалась
    /// СТАРАЯ cookie CsrfCookieNames.PublicToken от предыдущей сессии — IAntiforgery.
    /// GetAndStoreTokens привязывает токен к HttpContext.User НА МОМЕНТ ВЫДАЧИ (см.
    /// PwaSessionCookieWriter), поэтому валидация такого токена на анонимном запросе (User
    /// ещё не аутентифицирован — самого логина не произошло) детерминированно проваливается с
    /// "meant for a different claims-based user", а не с ошибкой протухшего/непонятного ключа
    /// (которую раньше маскировала ротация эфемерных ключей Data Protection при каждом
    /// редеплое — токен предыдущей сессии и без того не расшифровывался, тем же кодом ошибки
    /// ниже). CSRF по своей природе защищает только УЖЕ аутентифицированное действие — у
    /// анонимного запроса нет сессии, которую можно было бы "прокатить" межсайтовой подделкой,
    /// поэтому гейт применяется, только если текущий запрос сам уже аутентифицирован.
    ///
    /// Отладка (прогрев кэша поиска, деплой): гейт ложно ронял МУТИРУЮЩИЕ запросы /api/admin/*
    /// (401→400 "csrf_token_invalid") у админа, который в том же браузере ещё и залогинен в PWA
    /// своим обычным аккаунтом — оттуда живёт cookie CsrfCookieNames.PublicToken. Раньше условие
    /// смотрело только на "аутентифицирован ли текущий запрос ХОТЬ КАК-ТО" — для группы
    /// /api/admin/* политика "PlatformAdmin" явно перечисляет AuthSchemes.Admin, поэтому
    /// PolicyEvaluator ПОЛНОСТЬЮ подменяет context.User на принципала одной этой схемы (см.
    /// AuthorizationMiddleware/PolicyEvaluator.AuthenticateAsync — PWA-принципал по умолчанию
    /// отбрасывается, не мёржится); IsAuthenticated при этом всё равно true (админ аутентифицирован
    /// как админ), и валидация антифорджери-токена, привязанного к PWA-пользователю, детерминированно
    /// проваливается на чужом принципале ("meant for a different claims-based user" — тот же код
    /// ошибки, что уже описан выше для другого сценария). CSRF-cookie/токен этого механизма выдаётся
    /// ТОЛЬКО PwaSessionCookieWriter.IssueCsrfCookie при PWA-сессии — гейт должен защищать именно её,
    /// поэтому условие сужено до "текущий принципал реально несёт identity схемы PwaCookie", а не
    /// "аутентифицирован хоть какой-нибудь схемой". Admin/TelegramMiniApp/Dev — не эта схема,
    /// пропускаются, даже если в браузере болтается чужая PWA CSRF-cookie.</summary>
    public static WebApplication UseFamilyHubCsrfGate(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var method = context.Request.Method;
            var isMutating = HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
                || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
            if (isMutating && context.Request.Path.StartsWithSegments("/api")
                && context.User.Identities.Any(i => i.IsAuthenticated && i.AuthenticationType == AuthSchemes.PwaCookie)
                && context.Request.Cookies.ContainsKey(CsrfCookieNames.PublicToken))
            {
                var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
                if (!await antiforgery.IsRequestValidAsync(context))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new { code = "csrf_token_invalid" });
                    return;
                }
            }
            await next();
        });

        return app;
    }
}
