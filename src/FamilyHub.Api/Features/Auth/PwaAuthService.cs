using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.ValueObjects;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Api.Features.Auth;

public enum ConfirmRegistrationResult { Success, InvalidCode, EmailTaken, WeakPassword, InvalidUsername, UsernameTaken }

public enum LoginResult { Success, InvalidCredentials, LockedOut }

public enum LinkEmailResult { Success, InvalidCode, EmailTaken, WeakPassword }

public enum ResetPasswordResult { Success, InvalidCode, WeakPassword }

public enum ChangePasswordResult { Success, NoPassword, InvalidCurrentPassword, WeakPassword }

/// <summary>
/// PWA-вход (этап 2 п.2.4): регистрация email → код на почту → пароль; вход email+пароль.
/// Lockout входа (15 мин после 5 неудачных попыток) поверх IP-rate-limit'а эндпоинтов.
/// Анти-enumeration: register/start всегда отвечает 200, существование email не раскрывается.
/// Выпуск/проверка email-кодов — общий <see cref="EmailOtpService"/> (используется также
/// Telegram-привязкой, см. TelegramBindingService).
/// </summary>
public class PwaAuthService(AppDbContext db, EmailOtpService otp, ILogger<PwaAuthService> logger)
{
    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public Task<StartCodeResult> StartRegistrationAsync(string rawEmail, CancellationToken ct = default) =>
        otp.IssueCodeAsync(NormalizeEmail(rawEmail), EmailCodePurpose.Register, userId: null, ct);

    public Task<StartCodeResult> StartLinkEmailAsync(Guid userId, string rawEmail, CancellationToken ct = default) =>
        otp.IssueCodeAsync(NormalizeEmail(rawEmail), EmailCodePurpose.LinkEmail, userId, ct);

    /// <summary>
    /// Забыли пароль: анти-enumeration в ОТВЕТЕ (всегда Sent, как и у регистрации) — но, в
    /// отличие от register/start, письмо реально уходит только на email существующего
    /// PWA-аккаунта. Иначе владелец постороннего адреса получал бы непонятное "код для сброса
    /// пароля" без всякого аккаунта — при регистрации это письмо хотя бы уместно (человек как
    /// раз и пытается создать аккаунт с этим адресом), при сбросе пароля уместности нет.
    /// </summary>
    public async Task<StartCodeResult> StartResetPasswordAsync(string rawEmail, CancellationToken ct = default)
    {
        var normalizedEmail = NormalizeEmail(rawEmail);
        if (!await db.Users.AnyAsync(u => u.Email == normalizedEmail && u.PasswordHash != null, ct))
            return StartCodeResult.Sent;

        return await otp.IssueCodeAsync(normalizedEmail, EmailCodePurpose.ResetPassword, userId: null, ct);
    }

    public async Task<bool> IsUsernameAvailableAsync(string rawUsername, CancellationToken ct = default)
    {
        var normalized = UsernameRules.Normalize(rawUsername);
        if (!UsernameRules.IsValid(normalized)) return false;
        return !await db.Users.AnyAsync(u => u.Username == normalized, ct);
    }

    public async Task<(ConfirmRegistrationResult Result, Guid UserId)> ConfirmRegistrationAsync(
        string rawEmail, string code, string password, string rawUsername, string? displayName, CancellationToken ct = default)
    {
        if (!PasswordRules.IsValid(password)) return (ConfirmRegistrationResult.WeakPassword, Guid.Empty);

        // Проверка username — ДО потребления email-кода: занятый хэндл не должен сжигать
        // 10-минутный код (пользователь иначе вынужден запрашивать письмо заново).
        var normalizedUsername = UsernameRules.Normalize(rawUsername);
        if (!UsernameRules.IsValid(normalizedUsername))
            return (ConfirmRegistrationResult.InvalidUsername, Guid.Empty);
        if (await db.Users.AnyAsync(u => u.Username == normalizedUsername, ct))
            return (ConfirmRegistrationResult.UsernameTaken, Guid.Empty);

        var normalizedEmail = NormalizeEmail(rawEmail);
        var verification = await otp.ConsumeCodeAsync(normalizedEmail, code, EmailCodePurpose.Register, ct);
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
            PasswordHash = PasswordHasher.Hash(password),
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
        string rawEmail, string password, CancellationToken ct = default)
    {
        var normalizedEmail = NormalizeEmail(rawEmail);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail && u.PasswordHash != null, ct);
        if (user is null)
        {
            // Выравнивание времени ответа: не раскрываем таймингом, существует ли аккаунт.
            PasswordHasher.Verify(password, PasswordHasher.Hash("Dummy0000"));
            return (LoginResult.InvalidCredentials, null, null);
        }

        if (user.LockedUntil is { } lockedUntil && lockedUntil > DateTime.UtcNow)
            return (LoginResult.LockedOut, null, lockedUntil);

        // Намеренно НЕТ проверки PasswordRules.IsValid здесь: вход проверяет только совпадение
        // хеша, а не формат ввода. Иначе аккаунты, у которых пароль был установлен ДО перехода
        // с 4-8-значного numeric PIN на текущую политику (см. PasswordRules), потеряли бы
        // возможность войти — старый хеш продолжает верифицироваться той же PasswordHasher,
        // формат хранения не менялся.
        if (!PasswordHasher.Verify(password, user.PasswordHash!))
        {
            user.FailedLoginAttempts++;
            DateTime? lockout = null;
            if (user.FailedLoginAttempts >= MaxFailedLogins)
            {
                lockout = DateTime.UtcNow.Add(LockoutDuration);
                user.LockedUntil = lockout;
                user.FailedLoginAttempts = 0;
                logger.LogWarning("PWA-вход: пользователь {UserId} заблокирован до {LockedUntil}", user.Id, lockout);
            }
            await db.SaveChangesAsync(ct);
            return lockout is null
                ? (LoginResult.InvalidCredentials, null, null)
                : (LoginResult.LockedOut, null, lockout);
        }

        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        await db.SaveChangesAsync(ct);
        return (LoginResult.Success, user, null);
    }

    public async Task<LinkEmailResult> ConfirmLinkEmailAsync(
        Guid userId, string rawEmail, string code, string password, CancellationToken ct = default)
    {
        if (!PasswordRules.IsValid(password)) return LinkEmailResult.WeakPassword;

        var normalizedEmail = NormalizeEmail(rawEmail);
        var verification = await otp.ConsumeCodeAsync(normalizedEmail, code, EmailCodePurpose.LinkEmail, ct);
        if (verification is null || verification.UserId != userId) return LinkEmailResult.InvalidCode;

        if (await db.Users.AnyAsync(u => u.Email == normalizedEmail && u.Id != userId, ct))
        {
            await db.SaveChangesAsync(ct);
            return LinkEmailResult.EmailTaken;
        }

        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        user.Email = normalizedEmail;
        user.PasswordHash = PasswordHasher.Hash(password);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("К аккаунту {UserId} привязан email для PWA-входа", userId);
        return LinkEmailResult.Success;
    }

    /// <summary>Сброс забытого пароля по email-коду; при успехе — сразу вход (как при регистрации).</summary>
    public async Task<(ResetPasswordResult Result, Guid UserId)> ConfirmResetPasswordAsync(
        string rawEmail, string code, string newPassword, CancellationToken ct = default)
    {
        if (!PasswordRules.IsValid(newPassword)) return (ResetPasswordResult.WeakPassword, Guid.Empty);

        var normalizedEmail = NormalizeEmail(rawEmail);
        var verification = await otp.ConsumeCodeAsync(normalizedEmail, code, EmailCodePurpose.ResetPassword, ct);
        if (verification is null) return (ResetPasswordResult.InvalidCode, Guid.Empty);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail && u.PasswordHash != null, ct);
        if (user is null)
        {
            // Код мог быть выпущен StartResetPasswordAsync для несуществующего аккаунта только
            // если аккаунт удалили в промежутке — крайне маловероятно, но на всякий случай не 500.
            await db.SaveChangesAsync(ct);
            return (ResetPasswordResult.InvalidCode, Guid.Empty);
        }

        user.PasswordHash = PasswordHasher.Hash(newPassword);
        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("PWA: пароль сброшен для пользователя {UserId}", user.Id);
        return (ResetPasswordResult.Success, user.Id);
    }

    /// <summary>
    /// Смена пароля из настроек (аутентифицированный пользователь знает текущий пароль) — в
    /// отличие от ConfirmResetPasswordAsync, не требует email-кода. Отзыв прочих сессий —
    /// забота вызывающего эндпоинта (см. AuthEndpoints.MapAuthEndpoints), не этого метода.
    /// </summary>
    public async Task<ChangePasswordResult> ChangePasswordAsync(
        Guid userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        if (!PasswordRules.IsValid(newPassword)) return ChangePasswordResult.WeakPassword;

        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        if (user.PasswordHash is null) return ChangePasswordResult.NoPassword;

        // Намеренно НЕТ проверки формата текущего пароля через PasswordRules — та же причина,
        // что в LoginAsync: старый хеш (в том числе ещё PIN-формата) должен продолжать
        // верифицироваться, даже если сам ввод больше не проходит текущие правила формата.
        if (!PasswordHasher.Verify(currentPassword, user.PasswordHash))
            return ChangePasswordResult.InvalidCurrentPassword;

        user.PasswordHash = PasswordHasher.Hash(newPassword);
        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("PWA: пароль изменён пользователем {UserId} из настроек", user.Id);
        return ChangePasswordResult.Success;
    }
}
