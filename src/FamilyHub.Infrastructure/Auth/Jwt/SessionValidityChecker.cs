using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FamilyHub.Infrastructure.Auth.Jwt;

/// <summary>
/// Жива ли PWA-сессия, на которую ссылается access-токен (claim SessionId) — аудит
/// security-audit-2026-10, M5. Без этой проверки выход, «завершить сеанс», смена/сброс пароля и
/// удаление аккаунта отзывали только refresh-токен, а уже выданный access продолжал работать до
/// конца AccessTokenLifetime (15 мин).
///
/// Сессия активна, если её строка не отозвана, ИЛИ она лишь ротирована (/refresh: RevokedAt +
/// ReplacedByTokenId) и сменившая её сессия жива — ротация не выход, access-токен, выданный до
/// неё, легитимен до своего срока. Двойная ротация за время жизни одного access-токена даёт 401 —
/// фронт просто сделает refresh ещё раз.
///
/// Результат кэшируется на <see cref="CacheDuration"/>, чтобы не ходить в БД на каждый запрос;
/// отзыв через TokenService в этом же процессе сбрасывает кэш сразу (<see cref="Invalidate"/>).
/// </summary>
public class SessionValidityChecker(AppDbContext db, IMemoryCache cache)
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public async Task<bool> IsActiveAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey(sessionId), out bool cached)) return cached;

        var active = await db.UserSessions.AsNoTracking()
            .Where(s => s.Id == sessionId)
            .AnyAsync(s => s.RevokedAt == null
                || (s.ReplacedByTokenId != null
                    && db.UserSessions.Any(r => r.Id == s.ReplacedByTokenId && r.RevokedAt == null)), ct);

        cache.Set(CacheKey(sessionId), active, CacheDuration);
        return active;
    }

    public static void Invalidate(IMemoryCache cache, IEnumerable<Guid> sessionIds)
    {
        foreach (var id in sessionIds) cache.Remove(CacheKey(id));
    }

    private static string CacheKey(Guid sessionId) => $"familyhub:session-active:{sessionId:N}";
}
