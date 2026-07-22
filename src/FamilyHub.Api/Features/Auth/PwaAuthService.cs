using System.Security.Cryptography;
using System.Text;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Email;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Api.Features.Auth;

public enum StartCodeResult { Sent, Throttled }

public enum ConfirmRegistrationResult { Success, InvalidCode, EmailTaken, WeakPin }

public enum LoginResult { Success, InvalidCredentials, LockedOut }

public enum LinkEmailResult { Success, InvalidCode, EmailTaken, WeakPin }

/// <summary>
/// PWA-вход (этап 2 п.2.4): регистрация email → код на почту → PIN; вход email+PIN.
/// Брутфорс-защита: лимит попыток кода (5 на код), lockout входа (15 мин после 5 неудач),
/// троттлинг выдачи кодов (3 активных в час на адрес) — поверх IP-rate-limit'а эндпоинтов.
/// Анти-enumeration: register/start всегда отвечает 200, существование email не раскрывается.
/// </summary>
public class PwaAuthService(AppDbContext db, IEmailSender email, ILogger<PwaAuthService> logger)
{
    private const int CodeTtlMinutes = 10;
    private const int MaxCodeAttempts = 5;
    private const int MaxActiveCodesPerHour = 3;
    private const int MaxFailedPins = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static bool IsValidPin(string pin) =>
        pin.Length is >= 4 and <= 8 && pin.All(char.IsAsciiDigit);

    private static string HashCode(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    public Task<StartCodeResult> StartRegistrationAsync(string rawEmail, CancellationToken ct = default) =>
        IssueCodeAsync(NormalizeEmail(rawEmail), EmailCodePurpose.Register, userId: null, ct);

    public Task<StartCodeResult> StartLinkEmailAsync(Guid userId, string rawEmail, CancellationToken ct = default) =>
        IssueCodeAsync(NormalizeEmail(rawEmail), EmailCodePurpose.LinkEmail, userId, ct);

    private async Task<StartCodeResult> IssueCodeAsync(
        string normalizedEmail, EmailCodePurpose purpose, Guid? userId, CancellationToken ct)
    {
        var hourAgo = DateTime.UtcNow.AddHours(-1);
        var recentCount = await db.EmailVerificationCodes.CountAsync(
            c => c.Email == normalizedEmail && c.Purpose == purpose && c.CreatedAt >= hourAgo && c.ConsumedAt == null, ct);
        if (recentCount >= MaxActiveCodesPerHour)
        {
            logger.LogWarning("Троттлинг кодов: {Purpose} для адреса (hash {EmailHash}) — лимит выдачи исчерпан",
                purpose, HashCode(normalizedEmail)[..8]);
            return StartCodeResult.Throttled;
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        db.EmailVerificationCodes.Add(new EmailVerificationCode
        {
            Id = Guid.NewGuid(),
            Email = normalizedEmail,
            CodeHash = HashCode(code),
            Purpose = purpose,
            UserId = userId,
            ExpiresAt = DateTime.UtcNow.AddMinutes(CodeTtlMinutes),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        await email.SendAsync(
            normalizedEmail,
            "FamilyHub: код подтверждения",
            $"Ваш код подтверждения: {code}\nКод действителен {CodeTtlMinutes} минут.", ct);

        return StartCodeResult.Sent;
    }

    /// <summary>Проверяет код и помечает потреблённым при успехе; инкрементирует Attempts при неверном вводе.</summary>
    private async Task<EmailVerificationCode?> ConsumeCodeAsync(
        string normalizedEmail, string code, EmailCodePurpose purpose, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var candidate = await db.EmailVerificationCodes
            .Where(c => c.Email == normalizedEmail && c.Purpose == purpose
                && c.ConsumedAt == null && c.ExpiresAt > now && c.Attempts < MaxCodeAttempts)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (candidate is null) return null;

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(candidate.CodeHash), Encoding.UTF8.GetBytes(HashCode(code))))
        {
            candidate.Attempts++;
            await db.SaveChangesAsync(ct);
            return null;
        }

        candidate.ConsumedAt = now;
        return candidate; // SaveChanges — на вызывающей стороне, одной транзакцией с бизнес-изменением.
    }

    public async Task<(ConfirmRegistrationResult Result, Guid UserId)> ConfirmRegistrationAsync(
        string rawEmail, string code, string pin, string? displayName, CancellationToken ct = default)
    {
        if (!IsValidPin(pin)) return (ConfirmRegistrationResult.WeakPin, Guid.Empty);

        var normalizedEmail = NormalizeEmail(rawEmail);
        var verification = await ConsumeCodeAsync(normalizedEmail, code, EmailCodePurpose.Register, ct);
        if (verification is null) return (ConfirmRegistrationResult.InvalidCode, Guid.Empty);

        if (await db.Users.AnyAsync(u => u.Email == normalizedEmail, ct))
        {
            await db.SaveChangesAsync(ct); // код всё равно потреблён — повторное использование бессмысленно
            return (ConfirmRegistrationResult.EmailTaken, Guid.Empty);
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = normalizedEmail,
            PinHash = PinHasher.Hash(pin),
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? normalizedEmail[..normalizedEmail.IndexOf('@')]
                : displayName.Trim(),
            CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("PWA-регистрация: создан пользователь {UserId}", user.Id);
        return (ConfirmRegistrationResult.Success, user.Id);
    }

    public async Task<(LoginResult Result, User? User, DateTime? LockedUntil)> LoginAsync(
        string rawEmail, string pin, CancellationToken ct = default)
    {
        var normalizedEmail = NormalizeEmail(rawEmail);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail && u.PinHash != null, ct);
        if (user is null)
        {
            // Выравнивание времени ответа: не раскрываем таймингом, существует ли аккаунт.
            PinHasher.Verify(pin, PinHasher.Hash("00000"));
            return (LoginResult.InvalidCredentials, null, null);
        }

        if (user.LockedUntil is { } lockedUntil && lockedUntil > DateTime.UtcNow)
            return (LoginResult.LockedOut, null, lockedUntil);

        if (!PinHasher.Verify(pin, user.PinHash!))
        {
            user.FailedPinAttempts++;
            DateTime? lockout = null;
            if (user.FailedPinAttempts >= MaxFailedPins)
            {
                lockout = DateTime.UtcNow.Add(LockoutDuration);
                user.LockedUntil = lockout;
                user.FailedPinAttempts = 0;
                logger.LogWarning("PWA-вход: пользователь {UserId} заблокирован до {LockedUntil}", user.Id, lockout);
            }
            await db.SaveChangesAsync(ct);
            return lockout is null
                ? (LoginResult.InvalidCredentials, null, null)
                : (LoginResult.LockedOut, null, lockout);
        }

        user.FailedPinAttempts = 0;
        user.LockedUntil = null;
        await db.SaveChangesAsync(ct);
        return (LoginResult.Success, user, null);
    }

    public async Task<LinkEmailResult> ConfirmLinkEmailAsync(
        Guid userId, string rawEmail, string code, string pin, CancellationToken ct = default)
    {
        if (!IsValidPin(pin)) return LinkEmailResult.WeakPin;

        var normalizedEmail = NormalizeEmail(rawEmail);
        var verification = await ConsumeCodeAsync(normalizedEmail, code, EmailCodePurpose.LinkEmail, ct);
        if (verification is null || verification.UserId != userId) return LinkEmailResult.InvalidCode;

        if (await db.Users.AnyAsync(u => u.Email == normalizedEmail && u.Id != userId, ct))
        {
            await db.SaveChangesAsync(ct);
            return LinkEmailResult.EmailTaken;
        }

        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        user.Email = normalizedEmail;
        user.PinHash = PinHasher.Hash(pin);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("К аккаунту {UserId} привязан email для PWA-входа", userId);
        return LinkEmailResult.Success;
    }
}
