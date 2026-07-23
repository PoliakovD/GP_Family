using System.Security.Cryptography;
using System.Text;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.ValueObjects;
using FamilyHub.Infrastructure.Email;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Api.Features.Auth;

public enum StartCodeResult { Sent, Throttled }

public enum ConfirmRegistrationResult { Success, InvalidCode, EmailTaken, WeakPin, InvalidUsername, UsernameTaken }

public enum LoginResult { Success, InvalidCredentials, LockedOut }

public enum LinkEmailResult { Success, InvalidCode, EmailTaken, WeakPin }

public enum ResetPinResult { Success, InvalidCode, WeakPin }

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

    /// <summary>
    /// Забыли PIN: анти-enumeration в ОТВЕТЕ (всегда Sent, как и у регистрации) — но, в отличие
    /// от register/start, письмо реально уходит только на email существующего PWA-аккаунта.
    /// Иначе владелец постороннего адреса получал бы непонятное "код для сброса PIN" без
    /// всякого аккаунта — при регистрации это письмо хотя бы уместно (человек как раз и
    /// пытается создать аккаунт с этим адресом), при сбросе PIN уместности нет.
    /// </summary>
    public async Task<StartCodeResult> StartResetPinAsync(string rawEmail, CancellationToken ct = default)
    {
        var normalizedEmail = NormalizeEmail(rawEmail);
        if (!await db.Users.AnyAsync(u => u.Email == normalizedEmail && u.PinHash != null, ct))
            return StartCodeResult.Sent;

        return await IssueCodeAsync(normalizedEmail, EmailCodePurpose.ResetPin, userId: null, ct);
    }

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

    public async Task<bool> IsUsernameAvailableAsync(string rawUsername, CancellationToken ct = default)
    {
        var normalized = UsernameRules.Normalize(rawUsername);
        if (!UsernameRules.IsValid(normalized)) return false;
        return !await db.Users.AnyAsync(u => u.Username == normalized, ct);
    }

    public async Task<(ConfirmRegistrationResult Result, Guid UserId)> ConfirmRegistrationAsync(
        string rawEmail, string code, string pin, string rawUsername, string? displayName, CancellationToken ct = default)
    {
        if (!IsValidPin(pin)) return (ConfirmRegistrationResult.WeakPin, Guid.Empty);

        // Проверка username — ДО потребления email-кода: занятый хэндл не должен сжигать
        // 10-минутный код (пользователь иначе вынужден запрашивать письмо заново).
        var normalizedUsername = UsernameRules.Normalize(rawUsername);
        if (!UsernameRules.IsValid(normalizedUsername))
            return (ConfirmRegistrationResult.InvalidUsername, Guid.Empty);
        if (await db.Users.AnyAsync(u => u.Username == normalizedUsername, ct))
            return (ConfirmRegistrationResult.UsernameTaken, Guid.Empty);

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
            Username = normalizedUsername,
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? normalizedEmail[..normalizedEmail.IndexOf('@')]
                : displayName.Trim(),
            CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Гонка на уникальном индексе Username между нашей проверкой и вставкой: код
            // ещё не потреблён на диске (SaveChanges для ConsumeCodeAsync шёл в этом же
            // вызове SaveChanges — откатился вместе с insert'ом), но снаружи это неотличимо
            // от "просто заново попробуй" — сообщаем UsernameTaken.
            logger.LogDebug(ex, "Гонка при регистрации: username {Username} занят параллельно", normalizedUsername);
            return (ConfirmRegistrationResult.UsernameTaken, Guid.Empty);
        }

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

    /// <summary>Сброс забытого PIN по email-коду; при успехе — сразу вход (как при регистрации).</summary>
    public async Task<(ResetPinResult Result, Guid UserId)> ConfirmResetPinAsync(
        string rawEmail, string code, string newPin, CancellationToken ct = default)
    {
        if (!IsValidPin(newPin)) return (ResetPinResult.WeakPin, Guid.Empty);

        var normalizedEmail = NormalizeEmail(rawEmail);
        var verification = await ConsumeCodeAsync(normalizedEmail, code, EmailCodePurpose.ResetPin, ct);
        if (verification is null) return (ResetPinResult.InvalidCode, Guid.Empty);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail && u.PinHash != null, ct);
        if (user is null)
        {
            // Код мог быть выпущен StartResetPinAsync для несуществующего аккаунта только если
            // аккаунт удалили в промежутке — крайне маловероятно, но на всякий случай не 500.
            await db.SaveChangesAsync(ct);
            return (ResetPinResult.InvalidCode, Guid.Empty);
        }

        user.PinHash = PinHasher.Hash(newPin);
        user.FailedPinAttempts = 0;
        user.LockedUntil = null;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("PWA: PIN сброшен для пользователя {UserId}", user.Id);
        return (ResetPinResult.Success, user.Id);
    }
}
