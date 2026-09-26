using System.Threading.RateLimiting;
using FamilyHub.Api.Features.Auth;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Modules.Medical.Extraction;
using Microsoft.AspNetCore.RateLimiting;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — rate limiting PWA-auth + конвейера
/// распознавания, без изменения поведения/порядка.
/// </summary>
public static class RateLimitingRegistration
{
    public static WebApplicationBuilder AddFamilyHubRateLimiting(this WebApplicationBuilder builder)
    {
        // --- Rate limiting PWA-auth (брутфорс-защита, этап 2 п.2.4). Лимиты конфигурируемы —
        // --- интеграционные тесты поднимают их, чтобы не ловить 429 на обычных сценариях.
        var authRateLimits = builder.Configuration.GetSection(AuthRateLimitOptions.SectionName).Get<AuthRateLimitOptions>() ?? new AuthRateLimitOptions();
        // --- Лимиты пайплайна распознавания (батч-загрузка, см. ExtractionLimitsOptions) — политики
        // --- "llm"/"medical-write" ниже используют то же значение секции.
        var extractionRateLimits = builder.Configuration.GetSection(ExtractionLimitsOptions.SectionName).Get<ExtractionLimitsOptions>() ?? new ExtractionLimitsOptions();
        builder.Services.AddRateLimiter(limiterOptions =>
        {
            limiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Партиция — по IP клиента: лимит общий для всех auth-эндпоинтов с этой политикой.
            limiterOptions.AddPolicy("auth", httpContext => RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = authRateLimits.AuthPermitLimit,
                    Window = TimeSpan.FromSeconds(authRateLimits.AuthWindowSeconds),
                    QueueLimit = 0,
                }));

            // Жёстче для выдачи email-кодов: каждая выдача — реальное письмо.
            limiterOptions.AddPolicy("auth-code", httpContext => RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = authRateLimits.CodePermitLimit,
                    Window = TimeSpan.FromSeconds(authRateLimits.CodeWindowSeconds),
                    QueueLimit = 0,
                }));

            // Погашение инвайт-кода — вне группы /api/auth, поэтому политика "auth" сюда не
            // распространяется; отдельная политика ради единообразия модели защиты (см. аудит,
            // находка 02.2), а не из-за реальной практичности перебора 128-битного кода.
            limiterOptions.AddPolicy("invite-redeem", httpContext => RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = authRateLimits.RedeemPermitLimit,
                    Window = TimeSpan.FromSeconds(authRateLimits.RedeemWindowSeconds),
                    QueueLimit = 0,
                }));

            // Партиция — по UserId (не по IP, как политики выше): все три политики стоят на
            // аутентифицированных эндпоинтах, а IP-партиция у NAT/офисной сети смешала бы разных
            // пользователей в один лимит (см. threat-model.md — уже задокументированная слабость
            // IP-партиции, здесь её не повторяем). UseRateLimiter() стоит ПОСЛЕ UseAuthentication/
            // UseAuthorization (см. ниже), поэтому claims уже доступны на момент партиционирования.
            // Фолбэк на IP — только на случай, если лимитер почему-то сработал раньше аутентификации.
            string UserOrIpPartitionKey(HttpContext httpContext) =>
                httpContext.User.GetUserId()?.ToString() ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            // Распознавание/OCR — один физический воркер LM Studio на всех пользователей
            // (LmStudioConcurrencyGate); без лимита на пользователя батч-загрузка одного человека могла бы
            // занять его на неопределённое время (см. class doc ExtractionLimitsOptions).
            limiterOptions.AddPolicy("llm", httpContext => RateLimitPartition.GetFixedWindowLimiter(
                UserOrIpPartitionKey(httpContext),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = extractionRateLimits.LlmPermitLimit,
                    Window = TimeSpan.FromSeconds(extractionRateLimits.LlmWindowSeconds),
                    QueueLimit = 0,
                }));

            // Публичная ссылка на отчёт для врача (анонимно, без аккаунта): партиция по IP. Токен —
            // 256 бит, перебор бессмыслен; лимит защищает от нагрузки (каждый запрос PDF — чтение и
            // расшифровка блоба) и от сканирования. Врачу хватает с запасом: страница = 2 запроса.
            limiterOptions.AddPolicy("public-report", httpContext => RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));

            // Создание мед-записи/загрузка вложения — дешевле LLM-вызова, но тоже неограничено сегодня.
            limiterOptions.AddPolicy("medical-write", httpContext => RateLimitPartition.GetFixedWindowLimiter(
                UserOrIpPartitionKey(httpContext),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = extractionRateLimits.MedicalWritePermitLimit,
                    Window = TimeSpan.FromSeconds(extractionRateLimits.MedicalWriteWindowSeconds),
                    QueueLimit = 0,
                }));
        });

        return builder;
    }
}
