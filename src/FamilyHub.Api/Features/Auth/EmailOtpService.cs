using System.Security.Cryptography;
using System.Text;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Email;
using FamilyHub.Infrastructure.Email.Templates;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Features.Auth;

public enum StartCodeResult { Sent, Throttled }

/// <summary>
/// Email-OTP: выпуск и проверка одноразовых 6-значных кодов подтверждения (общий механизм для
/// PWA-регистрации/сброса пароля/привязки email и для привязки Telegram-аккаунта — код в обоих
/// случаях уходит на почту, различается только <see cref="EmailCodePurpose"/>). Вынесено из
/// PwaAuthService, чтобы не дублировать троттлинг/хранение/сверку хеша кода во втором сервисе.
/// Брутфорс-защита: лимит попыток кода (5 на код), троттлинг выдачи (3 активных в час на адрес).
/// </summary>
public class EmailOtpService(
    AppDbContext db, IEmailSender email, EmailTemplateRenderer templates,
    IOptions<EmailOptions> emailOptions, ILogger<EmailOtpService> logger)
{
    private const int CodeTtlMinutes = 10; // 10 → «минут»; при изменении проверить склонение в CopyFor/тексте письма.
    private const int MaxCodeAttempts = 5;
    private const int MaxActiveCodesPerHour = 3;

    /// <summary>
    /// Тема + текст рамки письма на каждое назначение кода — раньше все четыре сценария слали
    /// одно и то же «FamilyHub: код подтверждения», и получатель не понимал, к какому действию
    /// код относится. Public static — переиспользуется EmailPreviewWriter/dev-эндпоинтом
    /// предпросмотра писем без похода в БД.
    /// </summary>
    public static (string Subject, EmailLayoutCopy Copy) CopyFor(EmailCodePurpose purpose) => purpose switch
    {
        EmailCodePurpose.Register => (
            "FamilyHub: код для регистрации",
            new EmailLayoutCopy(
                "Подтверждение регистрации",
                "Код действителен десять минут",
                "Введите этот код на странице регистрации, чтобы создать аккаунт FamilyHub.",
                "Никому не сообщайте этот код — сотрудники FamilyHub его не спрашивают.")),
        EmailCodePurpose.LinkEmail => (
            "FamilyHub: код для привязки email",
            new EmailLayoutCopy(
                "Привязка email",
                "Код действителен десять минут",
                "Введите этот код в приложении, чтобы привязать этот адрес к вашему аккаунту FamilyHub.",
                "Никому не сообщайте этот код — сотрудники FamilyHub его не спрашивают.")),
        EmailCodePurpose.ResetPassword => (
            "FamilyHub: код для сброса пароля",
            new EmailLayoutCopy(
                "Сброс пароля",
                "Код действителен десять минут",
                "Введите этот код на странице восстановления, чтобы задать новый пароль.",
                "Никому не сообщайте этот код — сотрудники FamilyHub его не спрашивают.")),
        EmailCodePurpose.TelegramBind => (
            "FamilyHub: код для входа через Telegram",
            new EmailLayoutCopy(
                "Вход через Telegram",
                "Код действителен десять минут",
                "Введите этот код в Telegram, чтобы подтвердить адрес и открыть доступ к данным семьи.",
                "Никому не сообщайте этот код — сотрудники FamilyHub его не спрашивают.")),
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, null),
    };

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

        var (subject, copy) = CopyFor(purpose);
        var html = templates.RenderCode(copy, code, CodeTtlMinutes);

        // Текстовая часть: строка с кодом сохранена ДОСЛОВНО — на неё опирается
        // CapturingEmailSender.LastCodeFor (\d{6} по первому совпадению) и завязанные на неё
        // юнит-тесты. Вводная фраза цифр не содержит, поэтому первая шестизначная
        // последовательность в теле — всё ещё сам код, не что-то из ссылки.
        var text =
            $"{copy.Intro}\n\n" +
            $"Ваш код подтверждения: {code}\n" +
            $"Код действителен {CodeTtlMinutes} минут.\n\n" +
            $"Открыть FamilyHub: {emailOptions.Value.PublicSiteUrl}";

        await email.SendAsync(normalizedEmail, subject, new EmailBody(text, html), ct);

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
