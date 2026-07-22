namespace FamilyHub.Api.Features.Auth;

/// <summary>
/// Лимиты rate limiting для auth-эндпоинтов (секция "RateLimiting"). Вынесены в конфиг,
/// чтобы интеграционные тесты могли поднять пороги (иначе обычные сценарии ловили бы 429),
/// а тест брутфорс-защиты — наоборот, занизить.
/// </summary>
public class AuthRateLimitOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Запросов на /api/auth/* с одного IP за окно.</summary>
    public int AuthPermitLimit { get; set; } = 10;

    public int AuthWindowSeconds { get; set; } = 60;

    /// <summary>Выдач email-кодов с одного IP за окно (каждая — реальное письмо).</summary>
    public int CodePermitLimit { get; set; } = 3;

    public int CodeWindowSeconds { get; set; } = 3600;
}
