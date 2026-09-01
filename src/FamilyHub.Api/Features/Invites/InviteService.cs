using System.Security.Cryptography;
using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Messaging;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Api.Features.Invites;

public record CreateInviteRequest(Guid? TargetUserId, FamilyRole AssignedRole, int MaxUses, DateTime? ExpiresAt);

public record PendingMemberDto(
    Guid UserId, string? LastName, string? FirstName, string? MiddleName, string? Username, FamilyRole Role, DateTime JoinedAt);

/// <summary>Публичный, обезличенный превью инвайта для лендинга /join/:code — никаких участников
/// семьи и никакого email, только то, что нужно, чтобы гость решил, стоит ли создавать аккаунт.</summary>
public record InvitePreviewDto(string FamilyName, string? InviterName);

public enum InvitePreviewResult { Valid, NotFound, Revoked, Expired, Exhausted }

/// <summary>
/// Приглашения и одобрение заявок — дословно по разделу 8 брифа: гибридное одобрение
/// (персональный инвайт → Active сразу, ссылка → PendingApproval до одобрения админа),
/// инкремент UsedCount в одной транзакции с вступлением (защита от гонки на MaxUses).
/// </summary>
public class InviteService(AppDbContext db, IFamilyAccessService access, IDomainEventPublisher publisher, ILogger<InviteService> logger)
{
    public async Task<(CreateInviteResult Result, FamilyInvite? Invite)> CreateInviteAsync(
        Guid creatorUserId, Guid familyId, CreateInviteRequest request, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(creatorUserId, familyId, FamilyRole.Admin, ct))
        {
            logger.LogWarning(
                "Отказ создания инвайта: пользователь {UserId} не админ семьи {FamilyId}", creatorUserId, familyId);
            return (CreateInviteResult.Forbidden, null);
        }

        var invite = new FamilyInvite
        {
            Id = Guid.NewGuid(),
            FamilyId = familyId,
            CreatedByUserId = creatorUserId,
            Code = GenerateCode(),
            TargetUserId = request.TargetUserId,
            AssignedRole = request.AssignedRole,
            MaxUses = request.TargetUserId is not null ? 1 : Math.Max(1, request.MaxUses),
            UsedCount = 0,
            ExpiresAt = request.ExpiresAt,
            IsRevoked = false,
            CreatedAt = DateTime.UtcNow,
        };

        db.FamilyInvites.Add(invite);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Инвайт {InviteId} создан для семьи {FamilyId} пользователем {UserId} (TargetUserId={TargetUserId}, MaxUses={MaxUses})",
            invite.Id, familyId, creatorUserId, request.TargetUserId, invite.MaxUses);

        return (CreateInviteResult.Created, invite);
    }

    /// <summary>Анонимный превью инвайта (лендинг /join/:code) — без авторизации, без персональных
    /// данных участников семьи: только название семьи и имя пригласившего, чтобы гость понял, куда
    /// его зовут, до того как заведёт аккаунт.</summary>
    public async Task<(InvitePreviewResult Result, InvitePreviewDto? Preview)> GetPreviewAsync(
        string code, CancellationToken ct = default)
    {
        var invite = await (
            from i in db.FamilyInvites
            where i.Code == code
            join creator in db.Users on i.CreatedByUserId equals creator.Id into creators
            from creator in creators.DefaultIfEmpty()
            select new
            {
                i.IsRevoked,
                i.ExpiresAt,
                i.UsedCount,
                i.MaxUses,
                FamilyName = i.Family.Name,
                InviterFirstName = creator != null ? creator.FirstName : null,
                InviterLastName = creator != null ? creator.LastName : null,
            }).FirstOrDefaultAsync(ct);

        if (invite is null) return (InvitePreviewResult.NotFound, null);
        if (invite.IsRevoked) return (InvitePreviewResult.Revoked, null);
        if (invite.ExpiresAt is { } exp && exp < DateTime.UtcNow) return (InvitePreviewResult.Expired, null);
        if (invite.UsedCount >= invite.MaxUses) return (InvitePreviewResult.Exhausted, null);

        var inviterName = string.Join(" ", new[] { invite.InviterFirstName, invite.InviterLastName }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        return (InvitePreviewResult.Valid,
            new InvitePreviewDto(invite.FamilyName, string.IsNullOrWhiteSpace(inviterName) ? null : inviterName));
    }

    public async Task<(RedeemResult Result, Guid? FamilyId)> RedeemInviteAsync(
        string code, Guid userId, CancellationToken ct = default)
    {
        var invite = await db.FamilyInvites.FirstOrDefaultAsync(i => i.Code == code, ct);

        if (invite is null)
        {
            logger.LogWarning("Погашение инвайта: код не найден (пользователь {UserId})", userId);
            return (RedeemResult.NotFound, null);
        }
        if (invite.IsRevoked)
        {
            logger.LogWarning("Погашение инвайта {InviteId} отклонено: отозван (пользователь {UserId})", invite.Id, userId);
            return (RedeemResult.Revoked, null);
        }
        if (invite.ExpiresAt is { } exp && exp < DateTime.UtcNow)
        {
            logger.LogWarning("Погашение инвайта {InviteId} отклонено: истёк {ExpiresAt} (пользователь {UserId})", invite.Id, exp, userId);
            return (RedeemResult.Expired, null);
        }
        if (invite.UsedCount >= invite.MaxUses)
        {
            logger.LogWarning(
                "Погашение инвайта {InviteId} отклонено: исчерпан лимит {UsedCount}/{MaxUses} (пользователь {UserId})",
                invite.Id, invite.UsedCount, invite.MaxUses, userId);
            return (RedeemResult.Exhausted, null);
        }
        if (invite.TargetUserId is { } target && target != userId)
        {
            logger.LogWarning(
                "Погашение инвайта {InviteId} отклонено: предназначен другому пользователю (запросил {UserId})", invite.Id, userId);
            return (RedeemResult.NotForYou, null);
        }

        var already = await db.FamilyMembers.AnyAsync(
            m => m.FamilyId == invite.FamilyId && m.UserId == userId, ct);
        if (already)
        {
            logger.LogDebug(
                "Погашение инвайта {InviteId}: пользователь {UserId} уже состоит в семье {FamilyId}",
                invite.Id, userId, invite.FamilyId);
            return (RedeemResult.AlreadyMember, invite.FamilyId);
        }

        // Гибрид: персональный инвайт → сразу Active; ссылка → PendingApproval.
        var status = invite.TargetUserId is not null
            ? MemberStatus.Active
            : MemberStatus.PendingApproval;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Атомарный инкремент-с-условием (аудит, находка Critical #1): проверка UsedCount выше
        // сама по себе не защищает от гонки — под READ COMMITTED (дефолт PostgreSQL) два
        // одновременных погашения одного и того же инвайта оба могли прочитать один и тот же
        // UsedCount ДО того, как любое из них закоммитило инкремент, и оба пройти проверку лимита
        // (обычная транзакция вокруг обычного `invite.UsedCount++` этого не предотвращает — она
        // лишь гарантирует атомарность СВОИХ собственных операций, а не видимость чужих).
        // ExecuteUpdateAsync с условием в WHERE компилируется в один UPDATE ... WHERE, атомарный
        // на уровне БД: второй конкурентный UPDATE над той же строкой блокируется до коммита
        // первого и видит уже увеличенный счётчик — affected == 0 означает, что лимит был
        // исчерпан параллельным запросом между нашей проверкой выше и этим моментом.
        var affected = await db.FamilyInvites
            .Where(i => i.Id == invite.Id && i.UsedCount < i.MaxUses)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.UsedCount, i => i.UsedCount + 1), ct);
        if (affected == 0)
        {
            await tx.RollbackAsync(ct);
            logger.LogWarning(
                "Погашение инвайта {InviteId} отклонено: лимит исчерпан параллельным запросом (пользователь {UserId})",
                invite.Id, userId);
            return (RedeemResult.Exhausted, null);
        }

        db.FamilyMembers.Add(new FamilyMember
        {
            Id = Guid.NewGuid(),
            FamilyId = invite.FamilyId,
            UserId = userId,
            Role = invite.AssignedRole,
            Status = status,
            JoinedAt = DateTime.UtcNow,
        });
        db.FamilyInviteRedemptions.Add(new FamilyInviteRedemption
        {
            Id = Guid.NewGuid(),
            FamilyInviteId = invite.Id,
            UserId = userId,
            RedeemedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation(
            "Инвайт {InviteId} погашен пользователем {UserId}, семья {FamilyId}, статус {Status}",
            invite.Id, userId, invite.FamilyId, status);

        return (status == MemberStatus.Active
            ? RedeemResult.Joined           // персональный — вступил сразу
            : RedeemResult.PendingApproval,  // ссылка — ждёт одобрения админа
            invite.FamilyId);
    }

    public async Task<RevokeInviteResult> RevokeInviteAsync(Guid inviteId, Guid requestingUserId, CancellationToken ct = default)
    {
        var invite = await db.FamilyInvites.FirstOrDefaultAsync(i => i.Id == inviteId, ct);
        if (invite is null)
        {
            logger.LogWarning("Отзыв инвайта {InviteId} отклонён: не найден (запросил {UserId})", inviteId, requestingUserId);
            return RevokeInviteResult.NotFound;
        }

        if (!await access.HasRoleAsync(requestingUserId, invite.FamilyId, FamilyRole.Admin, ct))
        {
            logger.LogWarning(
                "Отзыв инвайта {InviteId} отклонён: пользователь {UserId} не админ семьи {FamilyId}",
                inviteId, requestingUserId, invite.FamilyId);
            return RevokeInviteResult.Forbidden;
        }

        invite.IsRevoked = true;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Инвайт {InviteId} отозван пользователем {UserId}", inviteId, requestingUserId);
        return RevokeInviteResult.Revoked;
    }

    /// <summary>Заявки семьи, ожидающие одобрения. Видит только Admin.</summary>
    public async Task<(ApproveRejectResult Result, List<PendingMemberDto> Pending)> GetPendingMembersAsync(
        Guid familyId, Guid requestingUserId, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(requestingUserId, familyId, FamilyRole.Admin, ct))
            return (ApproveRejectResult.Forbidden, []);

        var pending = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.FamilyId == familyId && m.Status == MemberStatus.PendingApproval)
            .Select(m => new PendingMemberDto(
                m.UserId, m.User.LastName, m.User.FirstName, m.User.MiddleName, m.User.Username, m.Role, m.JoinedAt))
            .ToListAsync(ct);

        return (ApproveRejectResult.Success, pending);
    }

    // Админ подтверждает заявку (только Admin семьи).
    public async Task<ApproveRejectResult> ApproveMemberAsync(Guid familyId, Guid targetUserId, Guid requestingUserId, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(requestingUserId, familyId, FamilyRole.Admin, ct))
        {
            logger.LogWarning(
                "Одобрение заявки отклонено: {UserId} не админ семьи {FamilyId}", requestingUserId, familyId);
            return ApproveRejectResult.Forbidden;
        }

        var member = await db.FamilyMembers.FirstOrDefaultAsync(m =>
            m.FamilyId == familyId && m.UserId == targetUserId && m.Status == MemberStatus.PendingApproval, ct);
        if (member is null)
        {
            logger.LogWarning(
                "Одобрение заявки: заявка пользователя {TargetUserId} в семье {FamilyId} не найдена", targetUserId, familyId);
            return ApproveRejectResult.NotFound;
        }

        member.Status = MemberStatus.Active;
        // Событие фиксируется тем же SaveChangesAsync, что и смена статуса, — атомарно.
        await publisher.PublishAsync(new MemberApprovedEvent(familyId, targetUserId), ct);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Заявка пользователя {TargetUserId} в семью {FamilyId} одобрена админом {UserId}", targetUserId, familyId, requestingUserId);
        return ApproveRejectResult.Success;
    }

    // Админ отклоняет заявку → membership удаляется.
    // UsedCount инвайта намеренно НЕ декрементируется (раздел 8, "нюанс с MaxUses" — проще и предсказуемее).
    public async Task<ApproveRejectResult> RejectMemberAsync(Guid familyId, Guid targetUserId, Guid requestingUserId, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(requestingUserId, familyId, FamilyRole.Admin, ct))
        {
            logger.LogWarning(
                "Отклонение заявки отклонено: {UserId} не админ семьи {FamilyId}", requestingUserId, familyId);
            return ApproveRejectResult.Forbidden;
        }

        var member = await db.FamilyMembers.FirstOrDefaultAsync(m =>
            m.FamilyId == familyId && m.UserId == targetUserId && m.Status == MemberStatus.PendingApproval, ct);
        if (member is null)
        {
            logger.LogWarning(
                "Отклонение заявки: заявка пользователя {TargetUserId} в семье {FamilyId} не найдена", targetUserId, familyId);
            return ApproveRejectResult.NotFound;
        }

        db.FamilyMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Заявка пользователя {TargetUserId} в семью {FamilyId} отклонена админом {UserId}", targetUserId, familyId, requestingUserId);
        return ApproveRejectResult.Success;
    }

    private static string GenerateCode() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
