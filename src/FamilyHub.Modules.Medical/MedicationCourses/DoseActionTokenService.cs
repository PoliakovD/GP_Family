using System.Security.Cryptography;
using System.Text;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.MedicationCourses;

public enum DoseTokenResult
{
    Success,

    /// <summary>Токена нет, он просрочен или права получателя пропали — снаружи это одно и то же «нет такого».</summary>
    NotFound,

    /// <summary>Приём уже в другом состоянии или токен использован для другого действия.</summary>
    Conflict,
}

/// <summary>
/// Одноразовые токены кнопок push («Принял», «Отложить», «Пропустить», ADR-0015). Кнопку выполняет
/// service worker без сессии, поэтому авторизует её токен: 256 случайных бит, привязан к одному приёму
/// и получателю напоминания, живёт до конца срока приёма плюс сутки, в БД лежит только SHA-256 (как у
/// токена публичной ссылки отчёта врачу). Сам токен передаётся лишь в push-payload, зашифрованном
/// end-to-end (RFC 8291), — push-релей его не видит. Действие выполняется от имени получателя с теми же
/// проверками доступа, что и в приложении.
/// </summary>
public class DoseActionTokenService(AppDbContext db, DoseService doses)
{
    private static readonly TimeSpan MinLifetime = TimeSpan.FromHours(2);
    private static readonly TimeSpan PastScheduledLifetime = TimeSpan.FromHours(24);

    /// <summary>Выпускает токен на приём; возвращает сам токен (в БД сохраняется только его хеш).</summary>
    public async Task<string> IssueAsync(MedicationDose dose, Guid recipientUserId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var expiresAt = (dose.ScheduledAt ?? now) + PastScheduledLifetime;
        if (expiresAt < now + MinLifetime) expiresAt = now + MinLifetime;

        var token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        db.DoseActionTokens.Add(new DoseActionToken
        {
            Id = Guid.NewGuid(), TokenHash = Hash(token), DoseId = dose.Id, RecipientUserId = recipientUserId,
            ExpiresAt = expiresAt, CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return token;
    }

    /// <summary>Выполняет действие по токену. Повтор того же действия идемпотентен (кнопку могут нажать
    /// дважды или сработать ретрай); другое действие по уже использованному токену — Conflict.</summary>
    public async Task<DoseTokenResult> RedeemAsync(string token, DoseAction action, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128) return DoseTokenResult.NotFound;

        var hash = Hash(token);
        var row = await db.DoseActionTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (row is null || row.ExpiresAt < DateTime.UtcNow) return DoseTokenResult.NotFound;

        if (row.UsedAt is not null)
            return row.UsedAction == action ? DoseTokenResult.Success : DoseTokenResult.Conflict;

        var (result, _, _) = await doses.ApplyToDoseAsync(row.RecipientUserId, row.DoseId, action, ct);
        switch (result)
        {
            case DoseResult.Success:
                row.UsedAt = DateTime.UtcNow;
                row.UsedAction = action;
                await db.SaveChangesAsync(ct);
                return DoseTokenResult.Success;
            case DoseResult.Conflict:
                return DoseTokenResult.Conflict;
            default:
                // NotFound/Forbidden/Invalid: приём или права получателя исчезли — токен бесполезен.
                return DoseTokenResult.NotFound;
        }
    }

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
