using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.Medkits;

/// <summary>
/// Аптечка — семейный ресурс-контейнер (у семьи может быть несколько аптечек, каждая со
/// своим набором медикаментов). Принадлежит семье, видна всем активным членам по роли,
/// Member может добавлять/править. Списки всегда фильтруются по FamilyId (инвариант 1).
/// </summary>
public class MedkitService(AppDbContext db, IFamilyAccessService access)
{
    public async Task<(MedkitAccessResult Result, List<MedkitDto> Items)> GetForFamilyAsync(
        Guid familyId, Guid userId, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(userId, familyId, FamilyRole.Member, ct))
            return (MedkitAccessResult.Forbidden, []);

        var items = await db.Medkits.AsNoTracking()
            .Where(k => k.FamilyId == familyId)
            .Select(k => ToDto(k))
            .ToListAsync(ct);

        return (MedkitAccessResult.Success, items);
    }

    public async Task<(MedkitAccessResult Result, MedkitDto? Item)> CreateAsync(
        Guid familyId, Guid userId, CreateMedkitRequest request, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(userId, familyId, FamilyRole.Member, ct))
            return (MedkitAccessResult.Forbidden, null);

        var medkit = new Medkit
        {
            Id = Guid.NewGuid(),
            FamilyId = familyId,
            Name = request.Name,
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow,
        };

        db.Medkits.Add(medkit);
        await db.SaveChangesAsync(ct);

        return (MedkitAccessResult.Success, ToDto(medkit));
    }

    public async Task<MedkitAccessResult> UpdateAsync(
        Guid medkitId, Guid userId, UpdateMedkitRequest request, CancellationToken ct = default)
    {
        var medkit = await db.Medkits.FirstOrDefaultAsync(k => k.Id == medkitId, ct);
        if (medkit is null) return MedkitAccessResult.NotFound;

        if (!await access.HasRoleAsync(userId, medkit.FamilyId, FamilyRole.Member, ct))
            return MedkitAccessResult.Forbidden;

        medkit.Name = request.Name;

        await db.SaveChangesAsync(ct);
        return MedkitAccessResult.Success;
    }

    public async Task<MedkitAccessResult> DeleteAsync(Guid medkitId, Guid userId, CancellationToken ct = default)
    {
        var medkit = await db.Medkits.FirstOrDefaultAsync(k => k.Id == medkitId, ct);
        if (medkit is null) return MedkitAccessResult.NotFound;

        if (!await access.HasRoleAsync(userId, medkit.FamilyId, FamilyRole.Member, ct))
            return MedkitAccessResult.Forbidden;

        db.Medkits.Remove(medkit);
        await db.SaveChangesAsync(ct);
        return MedkitAccessResult.Success;
    }

    private static MedkitDto ToDto(Medkit k) =>
        new(k.Id, k.FamilyId, k.Name, k.CreatedByUserId, k.CreatedAt);
}
