using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Birthdays.Birthdays;

/// <summary>
/// Дни рождения — семейный ресурс (этап 4 п.11 брифа), по аналогии с Medication (раздел 4.1):
/// принадлежит семье, видна всем активным членам по роли, Member может добавлять/править.
/// Списки всегда фильтруются по FamilyId (инвариант 1) — никогда не грузим Birthday по Id
/// без проверки доступа к его семье.
/// </summary>
public class BirthdayService(AppDbContext db, IFamilyAccessService access, ILogger<BirthdayService> logger)
{
    public async Task<(BirthdayAccessResult Result, List<BirthdayDto> Items)> GetForFamilyAsync(
        Guid familyId, Guid userId, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(userId, familyId, FamilyRole.Member, ct))
        {
            logger.LogWarning("Список дней рождения отклонён: {UserId} не состоит в семье {FamilyId}", userId, familyId);
            return (BirthdayAccessResult.Forbidden, []);
        }

        var items = await db.Birthdays.AsNoTracking()
            .Where(b => b.FamilyId == familyId)
            .Select(b => ToDto(b))
            .ToListAsync(ct);

        logger.LogDebug("Загружено {Count} дней рождения семьи {FamilyId}", items.Count, familyId);
        return (BirthdayAccessResult.Success, items);
    }

    public async Task<(BirthdayAccessResult Result, BirthdayDto? Item)> CreateAsync(
        Guid familyId, Guid userId, CreateBirthdayRequest request, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(userId, familyId, FamilyRole.Member, ct))
        {
            logger.LogWarning("Создание дня рождения отклонено: {UserId} не состоит в семье {FamilyId}", userId, familyId);
            return (BirthdayAccessResult.Forbidden, null);
        }

        var birthday = new Birthday
        {
            Id = Guid.NewGuid(),
            FamilyId = familyId,
            PersonName = request.PersonName,
            Date = request.Date,
        };

        db.Birthdays.Add(birthday);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "День рождения {BirthdayId} ({PersonName}) создан пользователем {UserId} в семье {FamilyId}",
            birthday.Id, birthday.PersonName, userId, familyId);
        return (BirthdayAccessResult.Success, ToDto(birthday));
    }

    public async Task<BirthdayAccessResult> UpdateAsync(
        Guid birthdayId, Guid userId, UpdateBirthdayRequest request, CancellationToken ct = default)
    {
        var birthday = await db.Birthdays.FirstOrDefaultAsync(b => b.Id == birthdayId, ct);
        if (birthday is null)
        {
            logger.LogWarning("Обновление дня рождения {BirthdayId}: не найден (запросил {UserId})", birthdayId, userId);
            return BirthdayAccessResult.NotFound;
        }

        if (!await access.HasRoleAsync(userId, birthday.FamilyId, FamilyRole.Member, ct))
        {
            logger.LogWarning(
                "Обновление дня рождения {BirthdayId} отклонено: {UserId} не состоит в семье {FamilyId}",
                birthdayId, userId, birthday.FamilyId);
            return BirthdayAccessResult.Forbidden;
        }

        birthday.PersonName = request.PersonName;
        birthday.Date = request.Date;

        await db.SaveChangesAsync(ct);
        logger.LogInformation("День рождения {BirthdayId} обновлён пользователем {UserId}", birthdayId, userId);
        return BirthdayAccessResult.Success;
    }

    public async Task<BirthdayAccessResult> DeleteAsync(Guid birthdayId, Guid userId, CancellationToken ct = default)
    {
        var birthday = await db.Birthdays.FirstOrDefaultAsync(b => b.Id == birthdayId, ct);
        if (birthday is null)
        {
            logger.LogWarning("Удаление дня рождения {BirthdayId}: не найден (запросил {UserId})", birthdayId, userId);
            return BirthdayAccessResult.NotFound;
        }

        if (!await access.HasRoleAsync(userId, birthday.FamilyId, FamilyRole.Member, ct))
        {
            logger.LogWarning(
                "Удаление дня рождения {BirthdayId} отклонено: {UserId} не состоит в семье {FamilyId}",
                birthdayId, userId, birthday.FamilyId);
            return BirthdayAccessResult.Forbidden;
        }

        db.Birthdays.Remove(birthday);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("День рождения {BirthdayId} удалён пользователем {UserId}", birthdayId, userId);
        return BirthdayAccessResult.Success;
    }

    private static BirthdayDto ToDto(Birthday b) =>
        new(b.Id, b.FamilyId, b.PersonName, b.Date);
}
