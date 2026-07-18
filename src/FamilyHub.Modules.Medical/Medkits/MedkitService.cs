using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Medkits;

/// <summary>
/// Аптечка — семейный ресурс-контейнер (у семьи может быть несколько аптечек, каждая со
/// своим набором медикаментов). Принадлежит семье, видна всем активным членам по роли,
/// Member может добавлять/править. Списки всегда фильтруются по FamilyId (инвариант 1).
/// </summary>
public class MedkitService(AppDbContext db, IFamilyAccessService access, ILogger<MedkitService> logger)
{
    public async Task<(MedkitAccessResult Result, List<MedkitDto> Items)> GetForFamilyAsync(
        Guid familyId, Guid userId, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(userId, familyId, FamilyRole.Member, ct))
        {
            logger.LogWarning("Список аптечек отклонён: {UserId} не состоит в семье {FamilyId}", userId, familyId);
            return (MedkitAccessResult.Forbidden, []);
        }

        // Инлайним конструктор DTO прямо в Select (не через ToDto) — так EF Core транслирует
        // k.Medications.Count в коррелированный COUNT-подзапрос на уровне SQL, без лишнего
        // круглого рейса и без подгрузки самих медикаментов (Medications навигация не грузится).
        var items = await db.Medkits.AsNoTracking()
            .Where(k => k.FamilyId == familyId)
            .Select(k => new MedkitDto(k.Id, k.FamilyId, k.Name, k.CreatedByUserId, k.CreatedAt, k.Medications.Count))
            .ToListAsync(ct);

        logger.LogDebug("Загружено {Count} аптечек семьи {FamilyId}", items.Count, familyId);
        return (MedkitAccessResult.Success, items);
    }

    public async Task<(MedkitAccessResult Result, MedkitDto? Item)> CreateAsync(
        Guid familyId, Guid userId, CreateMedkitRequest request, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(userId, familyId, FamilyRole.Member, ct))
        {
            logger.LogWarning("Создание аптечки отклонено: {UserId} не состоит в семье {FamilyId}", userId, familyId);
            return (MedkitAccessResult.Forbidden, null);
        }

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

        logger.LogInformation(
            "Аптечка {MedkitId} ({Name}) создана пользователем {UserId} в семье {FamilyId}",
            medkit.Id, medkit.Name, userId, familyId);
        return (MedkitAccessResult.Success, ToDto(medkit));
    }

    public async Task<MedkitAccessResult> UpdateAsync(
        Guid medkitId, Guid userId, UpdateMedkitRequest request, CancellationToken ct = default)
    {
        var medkit = await db.Medkits.FirstOrDefaultAsync(k => k.Id == medkitId, ct);
        if (medkit is null)
        {
            logger.LogWarning("Обновление аптечки {MedkitId}: не найдена (запросил {UserId})", medkitId, userId);
            return MedkitAccessResult.NotFound;
        }

        if (!await access.HasRoleAsync(userId, medkit.FamilyId, FamilyRole.Member, ct))
        {
            logger.LogWarning(
                "Обновление аптечки {MedkitId} отклонено: {UserId} не состоит в семье {FamilyId}",
                medkitId, userId, medkit.FamilyId);
            return MedkitAccessResult.Forbidden;
        }

        medkit.Name = request.Name;

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Аптечка {MedkitId} обновлена пользователем {UserId}", medkitId, userId);
        return MedkitAccessResult.Success;
    }

    public async Task<MedkitAccessResult> DeleteAsync(Guid medkitId, Guid userId, CancellationToken ct = default)
    {
        var medkit = await db.Medkits.FirstOrDefaultAsync(k => k.Id == medkitId, ct);
        if (medkit is null)
        {
            logger.LogWarning("Удаление аптечки {MedkitId}: не найдена (запросил {UserId})", medkitId, userId);
            return MedkitAccessResult.NotFound;
        }

        if (!await access.HasRoleAsync(userId, medkit.FamilyId, FamilyRole.Member, ct))
        {
            logger.LogWarning(
                "Удаление аптечки {MedkitId} отклонено: {UserId} не состоит в семье {FamilyId}",
                medkitId, userId, medkit.FamilyId);
            return MedkitAccessResult.Forbidden;
        }

        db.Medkits.Remove(medkit);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Аптечка {MedkitId} удалена пользователем {UserId}", medkitId, userId);
        return MedkitAccessResult.Success;
    }

    // Используется только для новосозданной аптечки (CreateAsync) — Medications = [] по
    // умолчанию, Count = 0 корректен без обращения к БД.
    private static MedkitDto ToDto(Medkit k) =>
        new(k.Id, k.FamilyId, k.Name, k.CreatedByUserId, k.CreatedAt, k.Medications.Count);
}
