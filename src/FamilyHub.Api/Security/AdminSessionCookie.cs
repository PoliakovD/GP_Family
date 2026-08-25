using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace FamilyHub.Api.Security;

/// <summary>
/// Выпуск/проверка cookie сессии админ-панели (ADR-0009) — общая точка для
/// AdminSessionEndpoints (выпуск при логине) и AdminAuthenticationHandler (проверка на каждый
/// запрос), чтобы purpose-строка протектора не разошлась между ними. Полезной нагрузки, кроме
/// самого факта "сессия действительна", нет — единственный логин на всю панель, персональной
/// identity внутри cookie не несёт. Использует ITimeLimitedDataProtector: срок действия зашит
/// В САМ токен (не только в Cookie.Expires, который лишь подсказка браузеру) — Unprotect бросает
/// на просроченном/подделанном токене одним и тем же способом, отдельная проверка даты не нужна.
/// DataProtection уже персистится в Postgres (см. Program.cs) — сессия переживает редеплой.
/// </summary>
public static class AdminSessionCookie
{
    private const string Purpose = "FamilyHub.Admin.Session";
    private const string Payload = "admin";

    private static ITimeLimitedDataProtector CreateProtector(IDataProtectionProvider provider) =>
        provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();

    public static string Issue(IDataProtectionProvider provider, TimeSpan lifetime) =>
        CreateProtector(provider).Protect(Payload, DateTimeOffset.UtcNow.Add(lifetime));

    public static bool Validate(IDataProtectionProvider provider, string token)
    {
        try
        {
            return CreateProtector(provider).Unprotect(token) == Payload;
        }
        catch (CryptographicException)
        {
            // Просрочен или подделан — ITimeLimitedDataProtector не различает эти случаи в
            // типе исключения, и снаружи различать незачем: оба варианта — просто "войдите заново".
            return false;
        }
    }
}
