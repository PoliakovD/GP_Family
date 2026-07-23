using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Api.Features.Account;

/// <summary>
/// Слияние двух аккаунтов одного человека при привязке Telegram к существующему
/// веб/email-аккаунту, когда у этого Telegram уже была отдельная запись User (писал боту
/// раньше). Выживает ВСЕГДА target (веб/email-аккаунт, инициировавший привязку через код в
/// настройках) — source (Telegram-only) переносится в него и удаляется.
///
/// Полная инвентаризация ссылок на User.Id в схеме (см. план "UI/UX + Auth Rework"):
/// FamilyMember/Notification — FK-каскад, остальное — без FK, чистится явно здесь.
/// UserConsent и MedicalAccessAudit НЕ трогаются: FK-less юридические/аудиторские записи,
/// переживают удаление пользователя — тот же принцип, что в AccountService.DeleteAccountAsync.
/// </summary>
public class AccountMergeService(AppDbContext db, ILogger<AccountMergeService> logger)
{
    public async Task MergeAsync(Guid sourceUserId, Guid targetUserId, CancellationToken ct = default)
    {
        if (sourceUserId == targetUserId) return;

        var source = await db.Users.SingleAsync(u => u.Id == sourceUserId, ct);
        var target = await db.Users.SingleAsync(u => u.Id == targetUserId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await MergeFamilyMembershipsAsync(sourceUserId, targetUserId, ct);

        await db.Notifications.Where(n => n.UserId == sourceUserId).ExecuteUpdateAsync(
            s => s.SetProperty(n => n.UserId, targetUserId), ct);

        await db.MedicalRecords.Where(r => r.OwnerUserId == sourceUserId).ExecuteUpdateAsync(
            s => s.SetProperty(r => r.OwnerUserId, targetUserId), ct);

        await MergeFamilyMedicalSharesAsync(sourceUserId, targetUserId, ct);

        await db.Medkits.Where(m => m.CreatedByUserId == sourceUserId).ExecuteUpdateAsync(
            s => s.SetProperty(m => m.CreatedByUserId, targetUserId), ct);
        await db.Medications.Where(m => m.CreatedByUserId == sourceUserId).ExecuteUpdateAsync(
            s => s.SetProperty(m => m.CreatedByUserId, targetUserId), ct);

        await db.FamilyInvites.Where(i => i.CreatedByUserId == sourceUserId).ExecuteUpdateAsync(
            s => s.SetProperty(i => i.CreatedByUserId, targetUserId), ct);
        await db.FamilyInvites.Where(i => i.TargetUserId == sourceUserId).ExecuteUpdateAsync(
            s => s.SetProperty(i => i.TargetUserId, targetUserId), ct);

        await MergeInviteRedemptionsAsync(sourceUserId, targetUserId, ct);
        await MergeCompatibilityResultsAsync(sourceUserId, targetUserId, ct);

        // Незавершённые коды на source бессмысленны после слияния — сам аккаунт исчезает.
        await db.EmailVerificationCodes.Where(c => c.UserId == sourceUserId).ExecuteDeleteAsync(ct);
        await db.TelegramLinkCodes.Where(c => c.UserId == sourceUserId).ExecuteDeleteAsync(ct);

        // Запомнить перед удалением source — переносим на target ниже. Удаление ПЕРЕД
        // присвоением TelegramId обязательно: уникальный индекс на TelegramId иначе временно
        // конфликтует (одна и та же величина на двух строках одновременно).
        var telegramId = source.TelegramId;
        var tgUsername = source.TgUsername;
        var sourceUsername = source.Username;

        await db.Users.Where(u => u.Id == sourceUserId).ExecuteDeleteAsync(ct);

        target.TelegramId = telegramId;
        target.TgUsername = tgUsername;
        // Если у target ещё нет своего видимого username, а у source он был — теперь он
        // свободен (source-строка уже удалена) и может достаться target.
        if (target.Username is null && sourceUsername is not null)
            target.Username = sourceUsername;
        await db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);

        logger.LogInformation(
            "Слияние аккаунтов: {SourceUserId} (Telegram) объединён с {TargetUserId} (email)",
            sourceUserId, targetUserId);
    }

    /// <summary>
    /// Для семей, где оба состояли — оставляем строку target, повышая Role/Status до
    /// максимума из двух; строку source удаляем. Для семей, где состоял только source —
    /// просто переносим UserId (свободно, коллизии по (FamilyId, UserId) быть не может).
    /// </summary>
    private async Task MergeFamilyMembershipsAsync(Guid sourceUserId, Guid targetUserId, CancellationToken ct)
    {
        var sourceMemberships = await db.FamilyMembers.Where(m => m.UserId == sourceUserId).ToListAsync(ct);
        var targetFamilyIds = await db.FamilyMembers
            .Where(m => m.UserId == targetUserId)
            .Select(m => m.FamilyId)
            .ToListAsync(ct);
        var targetFamilyIdSet = targetFamilyIds.ToHashSet();

        foreach (var membership in sourceMemberships)
        {
            if (targetFamilyIdSet.Contains(membership.FamilyId))
            {
                var targetMembership = await db.FamilyMembers.SingleAsync(
                    m => m.FamilyId == membership.FamilyId && m.UserId == targetUserId, ct);
                if (membership.Role > targetMembership.Role) targetMembership.Role = membership.Role;
                if (membership.Status > targetMembership.Status) targetMembership.Status = membership.Status;
                db.FamilyMembers.Remove(membership);
            }
            else
            {
                membership.UserId = targetUserId;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task MergeFamilyMedicalSharesAsync(Guid sourceUserId, Guid targetUserId, CancellationToken ct)
    {
        var targetFamilyIds = await db.FamilyMedicalShares
            .Where(s => s.OwnerUserId == targetUserId)
            .Select(s => s.FamilyId)
            .ToListAsync(ct);

        await db.FamilyMedicalShares
            .Where(s => s.OwnerUserId == sourceUserId && targetFamilyIds.Contains(s.FamilyId))
            .ExecuteDeleteAsync(ct);
        await db.FamilyMedicalShares.Where(s => s.OwnerUserId == sourceUserId).ExecuteUpdateAsync(
            s => s.SetProperty(x => x.OwnerUserId, targetUserId), ct);
    }

    private async Task MergeInviteRedemptionsAsync(Guid sourceUserId, Guid targetUserId, CancellationToken ct)
    {
        var targetInviteIds = await db.FamilyInviteRedemptions
            .Where(r => r.UserId == targetUserId)
            .Select(r => r.FamilyInviteId)
            .ToListAsync(ct);

        await db.FamilyInviteRedemptions
            .Where(r => r.UserId == sourceUserId && targetInviteIds.Contains(r.FamilyInviteId))
            .ExecuteDeleteAsync(ct);
        await db.FamilyInviteRedemptions.Where(r => r.UserId == sourceUserId).ExecuteUpdateAsync(
            s => s.SetProperty(x => x.UserId, targetUserId), ct);
    }

    private async Task MergeCompatibilityResultsAsync(Guid sourceUserId, Guid targetUserId, CancellationToken ct)
    {
        var targetHashes = await db.PersonalCompatibilityResults
            .Where(r => r.UserId == targetUserId)
            .Select(r => r.InputHash)
            .ToListAsync(ct);

        await db.PersonalCompatibilityResults
            .Where(r => r.UserId == sourceUserId && targetHashes.Contains(r.InputHash))
            .ExecuteDeleteAsync(ct);
        await db.PersonalCompatibilityResults.Where(r => r.UserId == sourceUserId).ExecuteUpdateAsync(
            s => s.SetProperty(x => x.UserId, targetUserId), ct);
    }
}
