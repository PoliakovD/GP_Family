using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Infrastructure.Authorization;

public class FamilyAccessService(AppDbContext db) : IFamilyAccessService
{
    public async Task<bool> HasRoleAsync(Guid userId, Guid familyId, FamilyRole minRole, CancellationToken ct = default)
    {
        var membership = await db.FamilyMembers.AsNoTracking().FirstOrDefaultAsync(
            m => m.FamilyId == familyId && m.UserId == userId, ct);

        return membership is { Status: MemberStatus.Active } && membership.Role >= minRole;
    }

    public Task<List<Guid>> GetActiveFamilyIdsAsync(Guid userId, CancellationToken ct = default) =>
        db.FamilyMembers.AsNoTracking()
            .Where(m => m.UserId == userId && m.Status == MemberStatus.Active)
            .Select(m => m.FamilyId)
            .ToListAsync(ct);
}
