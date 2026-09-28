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

    /// <summary>Отдельный, более мягкий лимит для /me, /refresh, /logout(-all), /sessions* —
    /// это не попытки входа (перебор пароля/кода здесь бессмыслен), а обычный трафик уже вошедшей
    /// сессии: /me дёргается на каждой guard-навигации SPA. Раньше делили бакет с login (10/мин на
    /// IP) — общий IP за NAT или пара 429 подряд у одного пользователя гасили /me и ошибочно
    /// выглядели как "не аутентифицирован"/"профиль не заполнен" (см. TECH_DEBT.md #12).</summary>
    public int AuthSessionPermitLimit { get; set; } = 60;

    public int AuthSessionWindowSeconds { get; set; } = 60;

    /// <summary>Выдач email-кодов с одного IP за окно (каждая — реальное письмо).</summary>
    public int CodePermitLimit { get; set; } = 3;

    public int CodeWindowSeconds { get; set; } = 3600;

    /// <summary>Погашений инвайт-кода (POST /api/invites/{code}/redeem) с одного IP за окно —
    /// единственный «угадай-секрет» эндпоинт вне /api/auth без rate-limit (см. аудит
    /// module-review-2026-08-02/02-core-family-invites-members-account-consent.md, находка 2).
    /// Код — 128 бит, перебор непрактичен и без лимита; это про единообразие модели защиты.</summary>
    public int RedeemPermitLimit { get; set; } = 1;

    public int RedeemWindowSeconds { get; set; } = 5;
}
