using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FamilyHub.Api.Security;

/// <summary>
/// Сессии админ-панели в БД (аудит security-audit-2026-10, бэклог M3). Раньше отзыв выходом жил в
/// памяти процесса и терялся при рестарте, а «выйти на всех устройствах» не было вовсе. Проверка
/// активности кэшируется на <see cref="CacheDuration"/> (тот же приём, что SessionValidityChecker у
/// PWA): отзыв в этом процессе сбрасывает кэш сразу.
/// </summary>
public class AdminSessionStore(AppDbContext db, IMemoryCache cache, TimeProvider time)
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public async Task<AdminSession> CreateAsync(TimeSpan lifetime, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var session = new AdminSession
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            ExpiresAt = now.Add(lifetime),
            IpAddress = ipAddress,
            UserAgent = userAgent is { Length: > 512 } ? userAgent[..512] : userAgent,
        };
        db.AdminSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session;
    }

    public async Task<bool> IsActiveAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey(sessionId), out bool cached)) return cached;

        var now = time.GetUtcNow().UtcDateTime;
        var active = await db.AdminSessions.AsNoTracking()
            .AnyAsync(s => s.Id == sessionId && s.RevokedAt == null && s.ExpiresAt > now, ct);
        cache.Set(CacheKey(sessionId), active, CacheDuration);
        return active;
    }

    public async Task RevokeAsync(Guid sessionId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await db.AdminSessions
            .Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
        cache.Remove(CacheKey(sessionId));
    }

    /// <returns>Сколько живых сессий отозвано.</returns>
    public async Task<int> RevokeAllAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var ids = await db.AdminSessions
            .Where(s => s.RevokedAt == null && s.ExpiresAt > now)
            .Select(s => s.Id)
            .ToListAsync(ct);
        await db.AdminSessions
            .Where(s => ids.Contains(s.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
        foreach (var id in ids) cache.Remove(CacheKey(id));
        return ids.Count;
    }

    private static string CacheKey(Guid sessionId) => $"familyhub:admin-session-active:{sessionId:N}";
}
