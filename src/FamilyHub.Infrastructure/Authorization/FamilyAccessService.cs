using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Infrastructure.Authorization;

public class FamilyAccessService(AppDbContext db, ILogger<FamilyAccessService> logger) : IFamilyAccessService
{
    public async Task<bool> HasRoleAsync(Guid userId, Guid familyId, FamilyRole minRole, CancellationToken ct = default)
    {
        var membership = await db.FamilyMembers.AsNoTracking().FirstOrDefaultAsync(
            m => m.FamilyId == familyId && m.UserId == userId, ct);

        var allowed = membership is { Status: MemberStatus.Active } && membership.Role >= minRole;
        if (!allowed)
        {
            logger.LogDebug(
                "Проверка доступа: {UserId} к семье {FamilyId} требуется роль >= {MinRole}, фактически {ActualRole}/{ActualStatus}",
                userId, familyId, minRole, membership?.Role, membership?.Status);
        }

        return allowed;
    }

    public Task<List<Guid>> GetActiveFamilyIdsAsync(Guid userId, CancellationToken ct = default) =>
        db.FamilyMembers.AsNoTracking()
            .Where(m => m.UserId == userId && m.Status == MemberStatus.Active)
            .Select(m => m.FamilyId)
            .ToListAsync(ct);
}
