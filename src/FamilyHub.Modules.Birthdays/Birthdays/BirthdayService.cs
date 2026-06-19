using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Birthdays.Birthdays;

/// <summary>
/// Дни рождения — семейный ресурс (этап 4 п.11 брифа), по аналогии с Medication (раздел 4.1):
/// принадлежит семье, видна всем активным членам по роли, Member может добавлять/править.
/// Списки всегда фильтруются по FamilyId (инвариант 1) — никогда не грузим Birthday по Id
/// без проверки доступа к его семье.
/// </summary>
public class BirthdayService(AppDbContext db, IFamilyAccessService access)
{
    public async Task<(BirthdayAccessResult Result, List<BirthdayDto> Items)> GetForFamilyAsync(
        Guid familyId, Guid userId, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(userId, familyId, FamilyRole.Member, ct))
            return (BirthdayAccessResult.Forbidden, []);

        var items = await db.Birthdays.AsNoTracking()
            .Where(b => b.FamilyId == familyId)
            .Select(b => ToDto(b))
            .ToListAsync(ct);

        return (BirthdayAccessResult.Success, items);
    }

    public async Task<(BirthdayAccessResult Result, BirthdayDto? Item)> CreateAsync(
        Guid familyId, Guid userId, CreateBirthdayRequest request, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(userId, familyId, FamilyRole.Member, ct))
            return (BirthdayAccessResult.Forbidden, null);

        var birthday = new Birthday
        {
            Id = Guid.NewGuid(),
            FamilyId = familyId,
            PersonName = request.PersonName,
            Date = request.Date,
        };

        db.Birthdays.Add(birthday);
        await db.SaveChangesAsync(ct);

        return (BirthdayAccessResult.Success, ToDto(birthday));
    }

    public async Task<BirthdayAccessResult> UpdateAsync(
        Guid birthdayId, Guid userId, UpdateBirthdayRequest request, CancellationToken ct = default)
    {
        var birthday = await db.Birthdays.FirstOrDefaultAsync(b => b.Id == birthdayId, ct);
        if (birthday is null) return BirthdayAccessResult.NotFound;

        if (!await access.HasRoleAsync(userId, birthday.FamilyId, FamilyRole.Member, ct))
            return BirthdayAccessResult.Forbidden;

        birthday.PersonName = request.PersonName;
        birthday.Date = request.Date;

        await db.SaveChangesAsync(ct);
        return BirthdayAccessResult.Success;
    }

    public async Task<BirthdayAccessResult> DeleteAsync(Guid birthdayId, Guid userId, CancellationToken ct = default)
    {
        var birthday = await db.Birthdays.FirstOrDefaultAsync(b => b.Id == birthdayId, ct);
        if (birthday is null) return BirthdayAccessResult.NotFound;

        if (!await access.HasRoleAsync(userId, birthday.FamilyId, FamilyRole.Member, ct))
            return BirthdayAccessResult.Forbidden;

        db.Birthdays.Remove(birthday);
        await db.SaveChangesAsync(ct);
        return BirthdayAccessResult.Success;
    }

    private static BirthdayDto ToDto(Birthday b) =>
        new(b.Id, b.FamilyId, b.PersonName, b.Date);
}
