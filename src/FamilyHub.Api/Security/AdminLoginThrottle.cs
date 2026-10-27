using FamilyHub.Api.Configuration;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Security;

/// <summary>
/// Блокировка формы входа админ-панели после серии неудач (аудит security-audit-2026-10, M3). Раньше
/// перебор пароля сдерживал только rate-limit "auth" по IP, который обходится сменой адреса. Счётчик
/// глобальный и in-memory: инстанс API один, логин у панели тоже один, а после рестарта начать
/// перебор заново не выгоднее, чем ждать окончания блокировки.
/// </summary>
public class AdminLoginThrottle(IOptions<AdminOptions> options, TimeProvider time)
{
    private readonly Lock _gate = new();
    private int _failures;
    private DateTimeOffset _windowStartedAt;
    private DateTimeOffset? _lockedUntil;

    /// <summary>Сейчас вход заблокирован — пароль даже не проверяется.</summary>
    public bool IsLocked()
    {
        lock (_gate)
        {
            return _lockedUntil is { } until && time.GetUtcNow() < until;
        }
    }

    /// <returns>true, если эта неудача включила блокировку.</returns>
    public bool RegisterFailure()
    {
        var admin = options.Value;
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (now - _windowStartedAt > admin.LockoutDuration)
            {
                _windowStartedAt = now;
                _failures = 0;
            }

            _failures++;
            if (_failures < admin.MaxFailedLogins) return false;

            _lockedUntil = now.Add(admin.LockoutDuration);
            _failures = 0;
            _windowStartedAt = now;
            return true;
        }
    }

    public void RegisterSuccess()
    {
        lock (_gate)
        {
            _failures = 0;
            _lockedUntil = null;
        }
    }
}
