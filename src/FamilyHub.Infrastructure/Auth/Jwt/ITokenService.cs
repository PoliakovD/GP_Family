namespace FamilyHub.Infrastructure.Auth.Jwt;

/// <summary>Пара токенов, выданная при login/register/refresh.</summary>
public record IssuedSession(
    string AccessToken,
    DateTime AccessTokenExpiresAt,
    string RefreshToken,
    DateTime RefreshTokenExpiresAt);

/// <summary>
/// Выпуск/ротация/отзыв JWT-сессии PWA. Access-токен — короткоживущий, самодостаточный
/// (claims: sub/UserId, Email, AuthProvider="email", SessionId), refresh-токен —
/// долгоживущий, хранится только по хешу в БД (<see cref="Domain.Entities.UserSession"/>),
/// ротируется на каждый refresh. Только PWA — у Telegram Mini App нет сессий вообще
/// (initData проверяется заново на каждый запрос).
/// </summary>
public interface ITokenService
{
    Task<IssuedSession> IssueAsync(
        Guid userId, string email, string? createdByIp, string? deviceInfo, CancellationToken ct = default);

    /// <summary>
    /// Ротация: null, если сессия не найдена/истекла, либо была уже отозвана без признаков
    /// ротации (обычный logout). Если предъявлен refresh-токен, уже заменённый более новым
    /// (кража/повтор) — отзывает ВСЮ цепочку сессий пользователя и возвращает null.
    /// </summary>
    Task<IssuedSession?> RefreshAsync(
        string rawRefreshToken, string? createdByIp, string? deviceInfo, CancellationToken ct = default);

    /// <summary>Отзыв одной сессии (logout текущего устройства).</summary>
    Task RevokeAsync(string rawRefreshToken, CancellationToken ct = default);

    /// <summary>Отзыв всех активных сессий пользователя (logout-all / account erasure).</summary>
    Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default);
}
