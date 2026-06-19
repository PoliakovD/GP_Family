using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Api.Features.Members;

/// <summary>
/// Выгон и самовыход (раздел 8 брифа). Выгнать может только Admin; выйти — любой участник
/// без требования роли. В обоих случаях: последнего активного админа убрать нельзя,
/// и автоматически чистится FamilyMedicalShare ушедшего для этой семьи.
/// </summary>
public class MembershipService(AppDbContext db, IFamilyAccessService access)
{
    public async Task<RemoveMemberResult> RemoveMemberAsync(Guid familyId, Guid targetUserId, Guid requestingUserId, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(requestingUserId, familyId, FamilyRole.Admin, ct))
            return RemoveMemberResult.Forbidden;

        var outcome = await RemoveMembershipCoreAsync(familyId, targetUserId, ct);
        return outcome switch
        {
            CoreOutcome.NotFound => RemoveMemberResult.NotFound,
            CoreOutcome.LastAdmin => RemoveMemberResult.LastAdmin,
            _ => RemoveMemberResult.Removed,
        };
    }

    public async Task<LeaveFamilyResult> LeaveFamilyAsync(Guid familyId, Guid userId, CancellationToken ct = default)
    {
        var outcome = await RemoveMembershipCoreAsync(familyId, userId, ct);
        return outcome switch
        {
            CoreOutcome.NotFound => LeaveFamilyResult.NotFound,
            CoreOutcome.LastAdmin => LeaveFamilyResult.LastAdmin,
            _ => LeaveFamilyResult.Left,
        };
    }

    private enum CoreOutcome { Removed, NotFound, LastAdmin }

    private async Task<CoreOutcome> RemoveMembershipCoreAsync(Guid familyId, Guid targetUserId, CancellationToken ct)
    {
        var member = await db.FamilyMembers
            .FirstOrDefaultAsync(m => m.FamilyId == familyId && m.UserId == targetUserId, ct);
        if (member is null) return CoreOutcome.NotFound;

        if (member.Role == FamilyRole.Admin && member.Status == MemberStatus.Active)
        {
            var adminCount = await db.FamilyMembers.CountAsync(m =>
                m.FamilyId == familyId && m.Role == FamilyRole.Admin && m.Status == MemberStatus.Active, ct);
            if (adminCount <= 1) return CoreOutcome.LastAdmin;
        }

        // Вышел/выгнан → его анализы перестают быть видны этой семье. Сами записи и сканы остаются у владельца.
        var shares = db.FamilyMedicalShares.Where(s => s.FamilyId == familyId && s.OwnerUserId == targetUserId);
        db.FamilyMedicalShares.RemoveRange(shares);

        db.FamilyMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        return CoreOutcome.Removed;
    }
}
