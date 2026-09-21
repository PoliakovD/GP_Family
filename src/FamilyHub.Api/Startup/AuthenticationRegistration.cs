using FamilyHub.Api.Configuration;
using FamilyHub.Api.Security;
using FamilyHub.Infrastructure.Auth;
using FamilyHub.Infrastructure.Auth.Jwt;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — авторизация (fallback-политика +
/// PlatformAdmin), JWT PWA-сессия, CSRF (Antiforgery) и сама аутентификация (Telegram Mini App /
/// PWA-JWT / Dev-заглушка / Admin, Smart policy-selector), без изменения поведения/порядка.
/// Крупнейший цельный блок исходного Program.cs — оставлен ОДНИМ файлом (не разбит по схемам),
/// потому что все схемы регистрируются на одном and том же authBuilder и логически составляют
/// одну связную настройку "кто есть кто" — дробление добавило бы косвенности больше, чем убрало.
/// </summary>
public static class AuthenticationRegistration
{
    /// <param name="devTools">DevAuthEnabled управляет и регистрацией Dev-схемы, и Smart-селектором.</param>
    /// <param name="admin">Enabled управляет регистрацией Admin-схемы.</param>
    public static WebApplicationBuilder AddFamilyHubAuthentication(
        this WebApplicationBuilder builder, DevToolsOptions devTools, AdminOptions admin)
    {
        builder.Services.AddAuthorization(options =>
        {
            // Защита по умолчанию: любой эндпоинт без явной политики всё равно требует аутентификации.
            options.FallbackPolicy = options.DefaultPolicy;

            // Админ-панель (ADR-0009): единственная политика, допускающая схему AuthSchemes.Admin — она
            // никогда не участвует в FallbackPolicy/Smart-селекторе выше, поэтому обычные PWA/Telegram-
            // запросы её не видят вовсе, а /api/admin/* без этой политики не открылся бы ни одной схемой.
            options.AddPolicy("PlatformAdmin", policy => policy
                .AddAuthenticationSchemes(AuthSchemes.Admin)
                .RequireAuthenticatedUser());
        });

        // --- JWT PWA-сессия: access-токен в httpOnly cookie + refresh-токен в БД (ротация,      ---
        // --- reuse-detection, revoke-all). Fail-fast: ключ подписи обязателен во всех средах.   ---
        // --- Ротация ключа подписи — ADR-0009: активный подписывает НОВЫЕ токены, отставные     ---
        // --- (Jwt:PreviousSigningKeys) принимаются только на ВАЛИДАЦИЮ уже выданных.            ---
        var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        // Связка строится здесь (не лениво в DI/JwtBearer options factory) — битая конфигурация
        // (дубли keyId, некорректный base64) валит старт хоста сразу, см. JwtSigningKeyRing.
        var jwtSigningKeys = JwtSigningKeyRing.Build(jwtOptions);
        builder.Services.AddScoped<ITokenService, TokenService>();

        // --- CSRF: double-submit антифорджери-токен поверх SameSite=Lax для PWA-cookie сессии (аудит
        // --- module-review-2026-08-02/01-auth-identity.md, находка 4). Только PWA — Telegram Mini App
        // --- аутентифицируется явным initData в заголовке, ambient-cookie CSRF к нему неприменим.
        // --- Cookie.Name — приватная (httpOnly) половина токена; публичную, которую читает Angular
        // --- (withXsrfConfiguration), выставляет PwaSessionCookieWriter.IssueCsrfCookie отдельно.
        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "familyhub.csrf";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            // Path обязателен явно: без него браузер/CookieContainer скоупит cookie по RFC 6265
            // default-path (директория ПЕРВОГО запроса, который её выставил — например
            // "/api/auth/register", если сессия открыта регистрацией) и она не долетает до
            // остальных /api-путей на следующих мутирующих запросах.
            options.Cookie.Path = "/";
            options.HeaderName = "X-XSRF-TOKEN";
        });

        // --- Аутентификация: два окружения (этап 2 п.2.4) — Telegram Mini App и PWA-JWT,      ---
        // --- плюс Dev-заглушка (DevTools:DevAuthEnabled, НЕ привязана к ASPNETCORE_ENVIRONMENT —---
        // --- см. DevToolsOptions). Селектор "Smart" во всех средах:                           ---
        // --- tma-заголовок → Telegram; X-Dev-TelegramId (dev) → Dev; иначе → JWT.             ---
        var authBuilder = builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = AuthSchemes.Smart;
            options.DefaultAuthenticateScheme = AuthSchemes.Smart;
            options.DefaultChallengeScheme = AuthSchemes.Smart;
        });

        authBuilder.AddScheme<AuthenticationSchemeOptions, TelegramMiniAppAuthenticationHandler>(AuthSchemes.TelegramMiniApp, null);

        authBuilder.AddJwtBearer(AuthSchemes.PwaCookie, jwtBearerOptions =>
        {
            jwtBearerOptions.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwtOptions.Issuer,
                ValidateAudience = true,
                ValidAudience = jwtOptions.Audience,
                ValidateIssuerSigningKey = true,
                // Множественное число (ADR-0009): валидация пробует каждый ключ связки, а не только
                // активный — уже выданный токен, подписанный отставным ключом, остаётся валиден до
                // истечения AccessTokenLifetime, даже если Jwt:SigningKey уже сменился.
                IssuerSigningKeys = jwtSigningKeys,
                ValidateLifetime = true,
                ClockSkew = jwtOptions.ClockSkew,
            };
            jwtBearerOptions.Events = new JwtBearerEvents
            {
                // Access-токен ездит в httpOnly cookie, а не в заголовке Authorization —
                // PWA-запросы идут через withCredentials, не bearer-заголовок.
                OnMessageReceived = ctx =>
                {
                    if (ctx.Request.Cookies.TryGetValue(PwaCookieNames.AccessToken, out var accessToken))
                        ctx.Token = accessToken;
                    return Task.CompletedTask;
                },
                // SPA-API: вместо WWW-Authenticate-челленджа отдаём голый 401, как и раньше у cookie-схемы.
                OnChallenge = ctx =>
                {
                    ctx.HandleResponse();
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                },
            };
        });

        if (devTools.DevAuthEnabled)
        {
            authBuilder.AddScheme<AuthenticationSchemeOptions, DevAuthenticationHandler>(AuthSchemes.Dev, null);
        }

        // Регистрируется только если панель включена — та же осторожность, что у DevAuthenticationHandler
        // выше: без Admin:Enabled эндпоинты /api/admin/* вообще не примапятся (см. ниже после
        // app.Build()), поэтому без регистрации схемы не остаётся вообще никакого пути её вызвать.
        if (admin.Enabled)
        {
            authBuilder.AddScheme<AuthenticationSchemeOptions, AdminAuthenticationHandler>(AuthSchemes.Admin, null);
        }

        authBuilder.AddPolicyScheme(AuthSchemes.Smart, AuthSchemes.Smart, policyOptions =>
        {
            policyOptions.ForwardDefaultSelector = httpContext =>
            {
                var request = httpContext.Request;
                var hasInitData = request.Headers.Authorization.ToString().StartsWith("tma ", StringComparison.Ordinal)
                    || request.Headers.ContainsKey("X-Telegram-Init-Data");
                if (hasInitData) return AuthSchemes.TelegramMiniApp;
                if (devTools.DevAuthEnabled && request.Headers.ContainsKey("X-Dev-TelegramId")) return AuthSchemes.Dev;
                return AuthSchemes.PwaCookie;
            };
        });

        return builder;
    }
}
