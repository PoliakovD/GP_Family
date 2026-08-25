using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.ValueObjects;
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
///
/// GetForFamilyAsync (identity rework) отдаёт ОБЪЕДИНЁННЫЙ список из трёх источников —
/// то же самое, что видит ReminderScanJob (Infrastructure/Notifications), иначе пользователь
/// получал бы напоминание о ДР, которого нет ни на одном экране. Create/Update/Delete ниже
/// по-прежнему работают только с ручными записями (Birthday) — производные из User/
/// FamilyDependent на фронте отображаются без кнопок редактирования (BirthdaySource != Manual).
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

        var manual = await db.Birthdays.AsNoTracking()
            .Where(b => b.FamilyId == familyId)
            .Select(b => ToDto(b))
            .ToListAsync(ct);

        var members = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.FamilyId == familyId && m.Status == MemberStatus.Active && m.User.BirthDate != null)
            .Select(m => new { m.UserId, m.User.LastName, m.User.FirstName, m.User.MiddleName, BirthDate = m.User.BirthDate!.Value })
            .ToListAsync(ct);
        var memberDtos = members.Select(m => new BirthdayDto(
            m.UserId, familyId,
            PersonName.FormatOrDefault(m.LastName, m.FirstName, m.MiddleName, PersonNameStyle.Full, fallback: "Участник семьи"),
            m.BirthDate, BirthdaySource.Member)).ToList();

        // FirstName/LastName зашифрованы — материализуем сущности, форматируем в памяти (тот же
        // приём, что FamilyDependentService.GetForFamilyAsync).
        var dependents = await db.FamilyDependents.AsNoTracking()
            .Where(d => d.FamilyId == familyId && d.BirthDate != null)
            .ToListAsync(ct);
        var dependentDtos = dependents.Select(d => new BirthdayDto(
            d.Id, familyId,
            d.IsPet ? d.FirstName : PersonName.FormatOrDefault(d.LastName, d.FirstName, d.MiddleName, PersonNameStyle.Full, fallback: d.FirstName),
            d.BirthDate!.Value, BirthdaySource.Dependent)).ToList();

        var items = manual.Concat(memberDtos).Concat(dependentDtos)
            .OrderBy(b => b.Date.Month).ThenBy(b => b.Date.Day)
            .ToList();

        logger.LogDebug(
            "Загружено {Count} дней рождения семьи {FamilyId} (ручных {Manual}, участников {Members}, подопечных {Dependents})",
            items.Count, familyId, manual.Count, memberDtos.Count, dependentDtos.Count);
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
        new(b.Id, b.FamilyId, b.PersonName, b.Date, BirthdaySource.Manual);
}
