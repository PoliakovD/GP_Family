using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Api.Features.Families;

public record FamilySummary(Guid Id, string Name, FamilyRole MyRole, MemberStatus MyStatus);

public class FamilyService(AppDbContext db)
{
    /// <summary>Создатель семьи становится её первым админом, сразу Active.</summary>
    public async Task<Guid> CreateFamilyAsync(Guid creatorUserId, string name, CancellationToken ct = default)
    {
        var family = new Family
        {
            Id = Guid.NewGuid(),
            Name = name,
            PlanType = PlanType.Free,
            CreatedAt = DateTime.UtcNow,
        };

        db.Families.Add(family);
        db.FamilyMembers.Add(new FamilyMember
        {
            Id = Guid.NewGuid(),
            FamilyId = family.Id,
            UserId = creatorUserId,
            Role = FamilyRole.Admin,
            Status = MemberStatus.Active,
            JoinedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync(ct);
        return family.Id;
    }

    /// <summary>Семьи, где пользователь состоит (включая PendingApproval — там он "ждёт", но видит сам факт заявки).</summary>
    public Task<List<FamilySummary>> GetMyFamiliesAsync(Guid userId, CancellationToken ct = default) =>
        db.FamilyMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new FamilySummary(m.FamilyId, m.Family.Name, m.Role, m.Status))
            .ToListAsync(ct);
}
