namespace FamilyHub.Infrastructure.Auth.Jwt;

/// <summary>
/// Настройки JWT-сессии PWA (секция "Jwt"). Access-токен живёт в httpOnly cookie короткое
/// время; refresh-токен — в БД (<see cref="Domain.Entities.RefreshToken"/>), ротируется на
/// каждый /api/auth/refresh. Telegram Mini App эти настройки не использует (initData
/// проверяется отдельно, per-request, см. TelegramMiniAppAuthenticationHandler).
/// </summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>Секрет для подписи (HMAC-SHA256), минимум 32 байта. Обязателен вне Development.
    /// Активный ключ — им подписываются НОВЫЕ access-токены.</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>
    /// Идентификатор активного ключа — пишется в заголовок <c>kid</c> новых токенов (ADR-0009).
    /// Чисто информационный ярлык: валидация JWT не выбирает ключ по <c>kid</c>, а пробует все
    /// ключи связки (см. <c>IssuerSigningKeys</c> в Program.cs) — используется только для
    /// диагностики и для отчёта в админ-панели.
    /// </summary>
    public string ActiveKeyId { get; set; } = "v1";

    /// <summary>
    /// Отставные ключи подписи — принимаются при ВАЛИДАЦИИ уже выданных токенов, но никогда не
    /// используются для подписи новых (ADR-0009: смена Jwt:SigningKey не должна мгновенно
    /// разлогинивать всех активных пользователей). Access-токен живёт всего
    /// <see cref="AccessTokenLifetime"/> — обычно отставной ключ можно убрать из конфигурации
    /// уже через несколько минут после ротации, когда все токены, подписанные им, истекут.
    /// Env: <c>Jwt__PreviousSigningKeys__0__Id</c> / <c>Jwt__PreviousSigningKeys__0__Material</c>.
    /// </summary>
    public List<JwtKeyEntry> PreviousSigningKeys { get; set; } = [];

    public string Issuer { get; set; } = "FamilyHub";

    public string Audience { get; set; } = "FamilyHub.Pwa";

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Допуск на рассинхронизацию часов при проверке истечения access-токена. Вынесен в опции
    /// (а не захардкожен), чтобы тесты экспирации могли обнулить его — иначе короткоживущий
    /// тестовый токен (секунды) на практике оставался бы валиден ещё все 30с допуска.
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Один отставной ключ подписи — см. <see cref="JwtOptions.PreviousSigningKeys"/>.</summary>
public class JwtKeyEntry
{
    /// <summary>Ярлык ключа (для диагностики/админки) — сам JWT не выбирает ключ по нему.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Материал ключа (HMAC-SHA256) в base64 — тот же формат, что SigningKey.</summary>
    public string Material { get; set; } = string.Empty;
}
