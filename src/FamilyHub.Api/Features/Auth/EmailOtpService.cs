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

/// <summary>
/// Email-OTP: выпуск и проверка одноразовых 6-значных кодов подтверждения (общий механизм для
/// PWA-регистрации/сброса пароля/привязки email и для привязки Telegram-аккаунта — код в обоих
/// случаях уходит на почту, различается только <see cref="EmailCodePurpose"/>). Вынесено из
/// PwaAuthService, чтобы не дублировать троттлинг/хранение/сверку хеша кода во втором сервисе.
/// Брутфорс-защита: лимит попыток кода (5 на код), троттлинг выдачи (3 активных в час на адрес).
/// </summary>
public class EmailOtpService(AppDbContext db, IEmailSender email, ILogger<EmailOtpService> logger)
{
    private const int CodeTtlMinutes = 10;
    private const int MaxCodeAttempts = 5;
    private const int MaxActiveCodesPerHour = 3;

    public async Task<StartCodeResult> IssueCodeAsync(
        string normalizedEmail, EmailCodePurpose purpose, Guid? userId, CancellationToken ct = default)
    {
        var hourAgo = DateTime.UtcNow.AddHours(-1);
        var recentCount = await db.EmailVerificationCodes.CountAsync(
            c => c.Email == normalizedEmail && c.Purpose == purpose && c.CreatedAt >= hourAgo && c.ConsumedAt == null, ct);
        if (recentCount >= MaxActiveCodesPerHour)
        {
            logger.LogWarning("Троттлинг кодов: {Purpose} для адреса (hash {EmailHash}) — лимит выдачи исчерпан",
                purpose, TokenHasher.Hash(normalizedEmail)[..8]);
            return StartCodeResult.Throttled;
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        db.EmailVerificationCodes.Add(new EmailVerificationCode
        {
            Id = Guid.NewGuid(),
            Email = normalizedEmail,
            CodeHash = TokenHasher.Hash(code),
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
    public async Task<EmailVerificationCode?> ConsumeCodeAsync(
        string normalizedEmail, string code, EmailCodePurpose purpose, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var candidate = await db.EmailVerificationCodes
            .Where(c => c.Email == normalizedEmail && c.Purpose == purpose
                && c.ConsumedAt == null && c.ExpiresAt > now && c.Attempts < MaxCodeAttempts)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (candidate is null) return null;

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(candidate.CodeHash), Encoding.UTF8.GetBytes(TokenHasher.Hash(code))))
        {
            candidate.Attempts++;
            await db.SaveChangesAsync(ct);
            return null;
        }

        candidate.ConsumedAt = now;
        return candidate; // SaveChanges — на вызывающей стороне, одной транзакцией с бизнес-изменением.
    }
}
