using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Api.Features.Families;

public record FamilySummary(Guid Id, string Name, FamilyRole MyRole, MemberStatus MyStatus);

public record CurrentFamilyMember(Guid Id, string DisplayName, string? Username, DateTime JoinedAt, FamilyRole Role);

public class FamilyService(AppDbContext db, ILogger<FamilyService> logger)
{
    /// <summary>Создатель семьи становится её первым админом, сразу Active.</summary>
    public async Task<Guid> CreateFamilyAsync(Guid creatorUserId, string name, CancellationToken ct = default)
    {
        logger.LogDebug("Создание семьи {Name} пользователем {UserId}", name, creatorUserId);

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
        logger.LogInformation("Семья {FamilyId} ({Name}) создана пользователем {UserId}", family.Id, name, creatorUserId);
        return family.Id;
    }

    /// <summary>Семьи, где пользователь состоит (включая PendingApproval — там он "ждёт", но видит сам факт заявки).</summary>
    public async Task<List<FamilySummary>> GetMyFamiliesAsync(Guid userId, CancellationToken ct = default)
    {
        var result = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new FamilySummary(m.FamilyId, m.Family.Name, m.Role, m.Status))
            .ToListAsync(ct);

        logger.LogDebug("Пользователь {UserId} состоит в {Count} семьях", userId, result.Count);
        return result;
    }

    public async Task<List<CurrentFamilyMember>>  GetFamilyMembersAsync(Guid familyId, CancellationToken ct)
    {
        var result = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.FamilyId == familyId)
            .Include(m => m.User)
            .Select(m=>new CurrentFamilyMember(m.UserId,m.User.DisplayName,m.User.Username,m.JoinedAt,m.Role))
            .ToListAsync(ct);

        logger.LogDebug("Загружено {Count} участников семьи {FamilyId}", result.Count, familyId);
        return result;
    }
}
