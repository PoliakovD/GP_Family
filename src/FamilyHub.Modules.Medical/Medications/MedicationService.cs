using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Medications;

/// <summary>
/// Медикаменты внутри аптечки — семейный ресурс (раздел 4.1 брифа): аптечка принадлежит
/// семье, видна всем активным членам по роли, Member может добавлять/править. Списки всегда
/// фильтруются по MedkitId (инвариант 1) — никогда не грузим Medication по Id без проверки
/// доступа к его семье.
/// </summary>
public class MedicationService(AppDbContext db, IFamilyAccessService access, ILogger<MedicationService> logger)
{
    public async Task<(MedicationAccessResult Result, List<MedicationDto> Items)> GetForMedkitAsync(
        Guid medkitId, Guid userId, CancellationToken ct = default)
    {
        var medkit = await db.Medkits.AsNoTracking().FirstOrDefaultAsync(k => k.Id == medkitId, ct);
        if (medkit is null)
        {
            logger.LogWarning("Список медикаментов: аптечка {MedkitId} не найдена (запросил {UserId})", medkitId, userId);
            return (MedicationAccessResult.NotFound, []);
        }

        if (!await access.HasRoleAsync(userId, medkit.FamilyId, FamilyRole.Member, ct))
        {
            logger.LogWarning(
                "Список медикаментов отклонён: {UserId} не состоит в семье {FamilyId} аптечки {MedkitId}",
                userId, medkit.FamilyId, medkitId);
            return (MedicationAccessResult.Forbidden, []);
        }

        var entities = await db.Medications.AsNoTracking()
            .Where(m => m.MedkitId == medkitId)
            .ToListAsync(ct);

        // ToDto десериализует DataJson — это не транслируется в SQL, поэтому маппим в память
        // уже после загрузки (список медикаментов аптечки — не тот объём, где это критично).
        var items = entities.Select(ToDto).ToList();

        logger.LogDebug("Загружено {Count} медикаментов аптечки {MedkitId}", items.Count, medkitId);
        return (MedicationAccessResult.Success, items);
    }

    public async Task<(MedicationAccessResult Result, MedicationDto? Item)> CreateAsync(
        Guid medkitId, Guid userId, CreateMedicationRequest request, CancellationToken ct = default)
    {
        var medkit = await db.Medkits.AsNoTracking().FirstOrDefaultAsync(k => k.Id == medkitId, ct);
        if (medkit is null)
        {
            logger.LogWarning("Создание медикамента: аптечка {MedkitId} не найдена (запросил {UserId})", medkitId, userId);
            return (MedicationAccessResult.NotFound, null);
        }

        if (!await access.HasRoleAsync(userId, medkit.FamilyId, FamilyRole.Member, ct))
        {
            logger.LogWarning(
                "Создание медикамента отклонено: {UserId} не состоит в семье {FamilyId}", userId, medkit.FamilyId);
            return (MedicationAccessResult.Forbidden, null);
        }

        var medication = new Medication
        {
            Id = Guid.NewGuid(),
            MedkitId = medkitId,
            FamilyId = medkit.FamilyId,
            Name = request.Name,
            ExpiryDate = request.ExpiryDate,
            DataJson = SerializeData(request.Data),
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow,
        };

        db.Medications.Add(medication);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Медикамент {MedicationId} ({Name}) создан пользователем {UserId} в аптечке {MedkitId}",
            medication.Id, medication.Name, userId, medkitId);
        return (MedicationAccessResult.Success, ToDto(medication));
    }

    public async Task<MedicationAccessResult> UpdateAsync(
        Guid medicationId, Guid userId, UpdateMedicationRequest request, CancellationToken ct = default)
    {
        var medication = await db.Medications.FirstOrDefaultAsync(m => m.Id == medicationId, ct);
        if (medication is null)
        {
            logger.LogWarning("Обновление медикамента {MedicationId}: не найден (запросил {UserId})", medicationId, userId);
            return MedicationAccessResult.NotFound;
        }

        if (!await access.HasRoleAsync(userId, medication.FamilyId, FamilyRole.Member, ct))
        {
            logger.LogWarning(
                "Обновление медикамента {MedicationId} отклонено: {UserId} не состоит в семье {FamilyId}",
                medicationId, userId, medication.FamilyId);
            return MedicationAccessResult.Forbidden;
        }

        medication.Name = request.Name;
        medication.ExpiryDate = request.ExpiryDate;
        medication.DataJson = SerializeData(request.Data);

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Медикамент {MedicationId} обновлён пользователем {UserId}", medicationId, userId);
        return MedicationAccessResult.Success;
    }

    public async Task<MedicationAccessResult> DeleteAsync(Guid medicationId, Guid userId, CancellationToken ct = default)
    {
        var medication = await db.Medications.FirstOrDefaultAsync(m => m.Id == medicationId, ct);
        if (medication is null)
        {
            logger.LogWarning("Удаление медикамента {MedicationId}: не найден (запросил {UserId})", medicationId, userId);
            return MedicationAccessResult.NotFound;
        }

        if (!await access.HasRoleAsync(userId, medication.FamilyId, FamilyRole.Member, ct))
        {
            logger.LogWarning(
                "Удаление медикамента {MedicationId} отклонено: {UserId} не состоит в семье {FamilyId}",
                medicationId, userId, medication.FamilyId);
            return MedicationAccessResult.Forbidden;
        }

        db.Medications.Remove(medication);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Медикамент {MedicationId} удалён пользователем {UserId}", medicationId, userId);
        return MedicationAccessResult.Success;
    }

    private static MedicationDto ToDto(Medication m) =>
        new(m.Id, m.MedkitId, m.FamilyId, m.Name, m.ExpiryDate, DeserializeData(m.DataJson), m.CreatedByUserId, m.CreatedAt);

    private static string? SerializeData(Dictionary<string, string>? data) =>
        data is null || data.Count == 0 ? null : JsonSerializer.Serialize(data);

    private static Dictionary<string, string> DeserializeData(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
}
