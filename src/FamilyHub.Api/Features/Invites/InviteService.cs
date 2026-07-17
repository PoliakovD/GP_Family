using System.Security.Cryptography;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Api.Features.Invites;

public record CreateInviteRequest(Guid? TargetUserId, FamilyRole AssignedRole, int MaxUses, DateTime? ExpiresAt);

public record PendingMemberDto(Guid UserId, string DisplayName, string? Username, FamilyRole Role, DateTime JoinedAt);

/// <summary>
/// Приглашения и одобрение заявок — дословно по разделу 8 брифа: гибридное одобрение
/// (персональный инвайт → Active сразу, ссылка → PendingApproval до одобрения админа),
/// инкремент UsedCount в одной транзакции с вступлением (защита от гонки на MaxUses).
/// </summary>
public class InviteService(AppDbContext db, IFamilyAccessService access)
{
    public async Task<(CreateInviteResult Result, FamilyInvite? Invite)> CreateInviteAsync(
        Guid creatorUserId, Guid familyId, CreateInviteRequest request, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(creatorUserId, familyId, FamilyRole.Admin, ct))
            return (CreateInviteResult.Forbidden, null);

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

        return (CreateInviteResult.Created, invite);
    }

    public async Task<RedeemResult> RedeemInviteAsync(string code, Guid userId, CancellationToken ct = default)
    {
        var invite = await db.FamilyInvites.FirstOrDefaultAsync(i => i.Code == code, ct);

        if (invite is null) return RedeemResult.NotFound;
        if (invite.IsRevoked) return RedeemResult.Revoked;
        if (invite.ExpiresAt is { } exp && exp < DateTime.UtcNow) return RedeemResult.Expired;
        if (invite.UsedCount >= invite.MaxUses) return RedeemResult.Exhausted;
        if (invite.TargetUserId is { } target && target != userId) return RedeemResult.NotForYou;

        var already = await db.FamilyMembers.AnyAsync(
            m => m.FamilyId == invite.FamilyId && m.UserId == userId, ct);
        if (already) return RedeemResult.AlreadyMember;

        // Гибрид: персональный инвайт → сразу Active; ссылка → PendingApproval.
        var status = invite.TargetUserId is not null
            ? MemberStatus.Active
            : MemberStatus.PendingApproval;

        // Вступление + инкремент в ОДНОЙ транзакции (защита от гонки на MaxUses).
        await using var tx = await db.Database.BeginTransactionAsync(ct);

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
        invite.UsedCount++;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return status == MemberStatus.Active
            ? RedeemResult.Joined           // персональный — вступил сразу
            : RedeemResult.PendingApproval;  // ссылка — ждёт одобрения админа
    }

    public async Task<RevokeInviteResult> RevokeInviteAsync(Guid inviteId, Guid requestingUserId, CancellationToken ct = default)
    {
        var invite = await db.FamilyInvites.FirstOrDefaultAsync(i => i.Id == inviteId, ct);
        if (invite is null) return RevokeInviteResult.NotFound;

        if (!await access.HasRoleAsync(requestingUserId, invite.FamilyId, FamilyRole.Admin, ct))
            return RevokeInviteResult.Forbidden;

        invite.IsRevoked = true;
        await db.SaveChangesAsync(ct);
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
                m.UserId, m.User.DisplayName, m.User.Username, m.Role, m.JoinedAt))
            .ToListAsync(ct);

        return (ApproveRejectResult.Success, pending);
    }

    // Админ подтверждает заявку (только Admin семьи).
    public async Task<ApproveRejectResult> ApproveMemberAsync(Guid familyId, Guid targetUserId, Guid requestingUserId, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(requestingUserId, familyId, FamilyRole.Admin, ct))
            return ApproveRejectResult.Forbidden;

        var member = await db.FamilyMembers.FirstOrDefaultAsync(m =>
            m.FamilyId == familyId && m.UserId == targetUserId && m.Status == MemberStatus.PendingApproval, ct);
        if (member is null) return ApproveRejectResult.NotFound;

        member.Status = MemberStatus.Active;
        await db.SaveChangesAsync(ct);
        return ApproveRejectResult.Success;
    }

    // Админ отклоняет заявку → membership удаляется.
    // UsedCount инвайта намеренно НЕ декрементируется (раздел 8, "нюанс с MaxUses" — проще и предсказуемее).
    public async Task<ApproveRejectResult> RejectMemberAsync(Guid familyId, Guid targetUserId, Guid requestingUserId, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(requestingUserId, familyId, FamilyRole.Admin, ct))
            return ApproveRejectResult.Forbidden;

        var member = await db.FamilyMembers.FirstOrDefaultAsync(m =>
            m.FamilyId == familyId && m.UserId == targetUserId && m.Status == MemberStatus.PendingApproval, ct);
        if (member is null) return ApproveRejectResult.NotFound;

        db.FamilyMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        return ApproveRejectResult.Success;
    }

    private static string GenerateCode() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
