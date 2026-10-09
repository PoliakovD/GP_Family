using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace FamilyHub.Api.Security;

/// <summary>
/// Выпуск/проверка cookie сессии админ-панели (ADR-0009) — общая точка для
/// AdminSessionEndpoints (выпуск при логине) и AdminAuthenticationHandler (проверка на каждый
/// запрос), чтобы purpose-строка протектора не разошлась между ними. Полезной нагрузки, кроме
/// самого факта "сессия действительна", нет — единственный логин на всю панель, персональной
/// identity внутри cookie не несёт (только случайный id сессии для отзыва при выходе). Использует ITimeLimitedDataProtector: срок действия зашит
/// В САМ токен (не только в Cookie.Expires, который лишь подсказка браузеру) — Unprotect бросает
/// на просроченном/подделанном токене одним и тем же способом, отдельная проверка даты не нужна.
/// DataProtection уже персистится в Postgres (см. Program.cs) — сессия переживает редеплой.
/// </summary>
public static class AdminSessionCookie
{
    private const string Purpose = "FamilyHub.Admin.Session";
    // Формат полезной нагрузки — "admin:{sessionId}", id строки AdminSessions (AdminSessionStore,
    // аудит security-audit-2026-10, M3): по нему сессия отзывается на сервере.
    private const string PayloadPrefix = "admin:";

    private static ITimeLimitedDataProtector CreateProtector(IDataProtectionProvider provider) =>
        provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();

    /// <summary>Cookie для уже созданной строки AdminSessions (AdminSessionStore.CreateAsync).</summary>
    public static string Issue(IDataProtectionProvider provider, Guid sessionId, DateTimeOffset expiresAt) =>
        CreateProtector(provider).Protect(PayloadPrefix + sessionId.ToString("N"), expiresAt);

    /// <returns>Id и срок сессии, либо null — токен просрочен, подделан или старого формата.</returns>
    public static AdminSessionToken? Validate(IDataProtectionProvider provider, string token)
    {
        try
        {
            var payload = CreateProtector(provider).Unprotect(token, out var expiresAt);
            return payload.StartsWith(PayloadPrefix, StringComparison.Ordinal)
                && Guid.TryParseExact(payload[PayloadPrefix.Length..], "N", out var sessionId)
                    ? new AdminSessionToken(sessionId, expiresAt)
                    : null;
        }
        catch (CryptographicException)
        {
            // Просрочен или подделан — ITimeLimitedDataProtector не различает эти случаи в
            // типе исключения, и снаружи различать незачем: оба варианта — просто "войдите заново".
            return null;
        }
    }
}

public record AdminSessionToken(Guid Id, DateTimeOffset ExpiresAt);
