using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Api.Features.Families;

public record FamilySummary(Guid Id, string Name, FamilyRole MyRole, MemberStatus MyStatus);

public record CurrentFamilyMember(Guid Id, string DisplayName, string? Username, DateTime JoinedAt, FamilyRole Role);

public enum DeleteFamilyResult { Deleted, Forbidden, NotFound }

public class FamilyService(AppDbContext db, IFamilyAccessService access, ILogger<FamilyService> logger)
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

    /// <summary>Семьи, где пользователь состоит (включая PendingApproval — там он "ждёт", но видит сам факт заявки).
    /// Семьи, где пользователь админ, идут первыми (FamilyRole.Admin=1 > Member=0) — так на Главной/на
    /// странице «Семьи» видно в первую очередь то, чем управляешь.</summary>
    public async Task<List<FamilySummary>> GetMyFamiliesAsync(Guid userId, CancellationToken ct = default)
    {
        var result = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderByDescending(m => m.Role)
            .ThenBy(m => m.Family.Name)
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

    /// <summary>
    /// Удаляет семью целиком. Разрешено только активному Admin семьи.
    /// Большинство дочерних сущностей (участники, аптечки, медикаменты, дни рождения,
    /// инвайты и их погашения, оповещения) удаляются каскадом на уровне БД (см. конфигурации
    /// в FamilyHub.Infrastructure.Persistence.Configurations). FamilyMedicalShare и
    /// MedicalRecordHidden не связаны FK на Family — чистим их явно, как и при выгоне
    /// участника (см. MembershipService.RemoveMembershipCoreAsync).
    /// </summary>
    public async Task<DeleteFamilyResult> DeleteFamilyAsync(Guid familyId, Guid requestingUserId, CancellationToken ct = default)
    {
        var family = await db.Families.FirstOrDefaultAsync(f => f.Id == familyId, ct);
        if (family is null) return DeleteFamilyResult.NotFound;

        if (!await access.HasRoleAsync(requestingUserId, familyId, FamilyRole.Admin, ct))
        {
            logger.LogWarning(
                "Удаление семьи отклонено: {UserId} не админ семьи {FamilyId}", requestingUserId, familyId);
            return DeleteFamilyResult.Forbidden;
        }

        var shares = db.FamilyMedicalShares.Where(s => s.FamilyId == familyId);
        db.FamilyMedicalShares.RemoveRange(shares);

        var hidden = db.MedicalRecordHiddens.Where(h => h.FamilyId == familyId);
        db.MedicalRecordHiddens.RemoveRange(hidden);

        db.Families.Remove(family);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Семья {FamilyId} удалена пользователем {UserId}", familyId, requestingUserId);
        return DeleteFamilyResult.Deleted;
    }
}
