using System.Collections.Concurrent;

namespace FamilyHub.Api.Security;

/// <summary>
/// Отозванные (выходом) сессии админ-панели (аудит security-audit-2026-10, M3). Cookie сессии
/// самодостаточна (ITimeLimitedDataProtector), поэтому без этого реестра «Выйти» лишь стирал cookie
/// в браузере, а скопированный токен жил до конца SessionLifetime. Хранится в памяти до истечения
/// самого токена: после рестарта API реестр пуст — осознанный компромисс (инстанс один, токен живёт
/// не дольше SessionLifetime), зафиксирован в отчёте аудита.
/// </summary>
public class AdminSessionRevocations(TimeProvider time)
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _revoked = new();

    public void Revoke(Guid sessionId, DateTimeOffset expiresAt)
    {
        var now = time.GetUtcNow();
        foreach (var (id, until) in _revoked)
        {
            if (until <= now) _revoked.TryRemove(id, out _);
        }
        _revoked[sessionId] = expiresAt;
    }

    public bool IsRevoked(Guid sessionId) => _revoked.ContainsKey(sessionId);
}
