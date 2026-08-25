using System.Security.Cryptography;
using FamilyHub.Api.Features.Account;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.ValueObjects;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Api.Features.Auth;

public enum StartLinkTelegramResult { Started, AlreadyLinked }

public enum LinkTelegramResult { Linked, Merged, InvalidCode, TelegramAlreadyOnThisAccount }

public record TelegramLinkPeek(string MaskedEmail);

/// <summary>
/// Привязка Telegram-аккаунта к существующему email/PWA-аккаунту "с подтверждением с другой
/// стороны": веб-аккаунт генерирует одноразовый код, пользователь предъявляет его боту
/// (deep-link t.me/bot?start=link___&lt;code&gt;), бот просит подтвердить inline-кнопкой.
/// Если у Telegram уже есть отдельный аккаунт (писал боту раньше) — аккаунты сливаются
/// через AccountMergeService, выживает веб/email-аккаунт.
/// </summary>
public class TelegramLinkService(AppDbContext db, AccountMergeService merge, ILogger<TelegramLinkService> logger)
{
    private const int CodeTtlMinutes = 10;

    public async Task<(StartLinkTelegramResult Result, string? Code, DateTime? ExpiresAt)> StartAsync(
        Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        if (user.TelegramId is not null)
            return (StartLinkTelegramResult.AlreadyLinked, null, null);

        // Не более одного активного кода на пользователя — выдача нового аннулирует прежние.
        await db.TelegramLinkCodes.Where(c => c.UserId == userId && c.ConsumedAt == null)
            .ExecuteDeleteAsync(ct);

        var rawCode = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)); // 32 hex-символа
        var expiresAt = DateTime.UtcNow.AddMinutes(CodeTtlMinutes);
        db.TelegramLinkCodes.Add(new TelegramLinkCode
        {
            Id = Guid.NewGuid(),
            CodeHash = TokenHasher.Hash(rawCode),
            UserId = userId,
            ExpiresAt = expiresAt,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        return (StartLinkTelegramResult.Started, rawCode, expiresAt);
    }

    /// <summary>Для бота: показать, к какому аккаунту привяжется Telegram, до подтверждения.</summary>
    public async Task<TelegramLinkPeek?> PeekAsync(string rawCode, CancellationToken ct = default)
    {
        var linkCode = await FindActiveCodeAsync(rawCode, ct);
        if (linkCode is null) return null;

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == linkCode.UserId, ct);
        return new TelegramLinkPeek(MaskEmail(user.Email));
    }

    public async Task<LinkTelegramResult> ConfirmAsync(
        string rawCode, long telegramId, string? tgUsername, CancellationToken ct = default)
    {
        var linkCode = await FindActiveCodeAsync(rawCode, ct);
        if (linkCode is null) return LinkTelegramResult.InvalidCode;

        var target = await db.Users.SingleAsync(u => u.Id == linkCode.UserId, ct);
        if (target.TelegramId == telegramId)
        {
            linkCode.ConsumedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return LinkTelegramResult.TelegramAlreadyOnThisAccount;
        }

        var source = await db.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId, ct);
        LinkTelegramResult outcome;

        if (source is null)
        {
            target.TelegramId = telegramId;
            target.TgUsername = tgUsername;
            if (target.Username is null && !string.IsNullOrWhiteSpace(tgUsername))
            {
                var normalized = UsernameRules.Normalize(tgUsername);
                if (UsernameRules.IsValid(normalized) && !await db.Users.AnyAsync(u => u.Username == normalized, ct))
                    target.Username = normalized;
            }
            linkCode.ConsumedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            outcome = LinkTelegramResult.Linked;
            logger.LogInformation("Telegram {TelegramId} привязан к аккаунту {UserId}", telegramId, target.Id);
        }
        else
        {
            linkCode.ConsumedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await merge.MergeAsync(source.Id, target.Id, ct);
            outcome = LinkTelegramResult.Merged;
        }

        return outcome;
    }

    private async Task<TelegramLinkCode?> FindActiveCodeAsync(string rawCode, CancellationToken ct)
    {
        var hash = TokenHasher.Hash(rawCode);
        var now = DateTime.UtcNow;
        return await db.TelegramLinkCodes.FirstOrDefaultAsync(
            c => c.CodeHash == hash && c.ConsumedAt == null && c.ExpiresAt > now, ct);
    }

    private static string MaskEmail(string? email)
    {
        if (string.IsNullOrEmpty(email)) return "аккаунту без email";
        var at = email.IndexOf('@');
        if (at <= 0) return email;
        return $"{email[0]}***{email[at..]}";
    }
}
