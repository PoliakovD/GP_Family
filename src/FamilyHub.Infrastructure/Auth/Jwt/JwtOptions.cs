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

    /// <summary>Секрет для подписи (HMAC-SHA256), минимум 32 байта. Обязателен вне Development.</summary>
    public string SigningKey { get; set; } = string.Empty;

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
