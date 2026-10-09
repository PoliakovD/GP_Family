using System.Security.Cryptography;
using System.Text;
using FamilyHub.Api.Configuration;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Security;

/// <summary>
/// Второй фактор входа в админ-панель (аудит security-audit-2026-10, бэклог M3): код из
/// приложения-аутентификатора по секрету Admin:TotpSecret. Принимается текущий шаг ±1 (рассинхрон
/// часов телефона до 30 с), сравнение за постоянное время. Один и тот же код дважды не принимается:
/// запоминается последний использованный шаг (логин у панели один, инстанс API один — хватает памяти;
/// после рестарта код можно повторить только в пределах тех же ~90 с, и для этого всё равно нужен пароль).
/// </summary>
public class AdminTotpVerifier(IOptions<AdminOptions> options, TimeProvider time)
{
    private readonly Lock _gate = new();
    private long _lastAcceptedStep = long.MinValue;

    public bool Verify(string? code)
    {
        var secret = Totp.DecodeBase32(options.Value.TotpSecret);
        if (secret is null || code is null) return false;

        var normalized = code.Replace(" ", "");
        if (normalized.Length != Totp.Digits || !normalized.All(char.IsAsciiDigit)) return false;

        var current = Totp.TimeStep(time.GetUtcNow());
        var codeBytes = Encoding.ASCII.GetBytes(normalized);

        lock (_gate)
        {
            for (var step = current - 1; step <= current + 1; step++)
            {
                if (step <= _lastAcceptedStep) continue; // уже использованный (или более ранний) код
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Totp.Compute(secret, step)), codeBytes)) continue;

                _lastAcceptedStep = step;
                return true;
            }
        }
        return false;
    }
}
