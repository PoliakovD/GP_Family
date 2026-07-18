using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Api.Features.Families;

public record FamilySummary(Guid Id, string Name, FamilyRole MyRole, MemberStatus MyStatus);

public record CurrentFamilyMember(Guid Id, string DisplayName, string? Username, DateTime JoinedAt, FamilyRole Role);

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

    public async Task<List<CurrentFamilyMember>>  GetFamilyMembersAsync(Guid familyId, CancellationToken ct)
    {
        return await db.FamilyMembers.AsNoTracking()
            .Where(m => m.FamilyId == familyId)
            .Include(m => m.User)
            .Select(m=>new CurrentFamilyMember(m.Id,m.User.DisplayName,m.User.Username,m.JoinedAt,m.Role))
            .ToListAsync(ct);
    }
}
