using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.Medications;

/// <summary>
/// Медикаменты внутри аптечки — семейный ресурс (раздел 4.1 брифа): аптечка принадлежит
/// семье, видна всем активным членам по роли, Member может добавлять/править. Списки всегда
/// фильтруются по MedkitId (инвариант 1) — никогда не грузим Medication по Id без проверки
/// доступа к его семье.
/// </summary>
public class MedicationService(AppDbContext db, IFamilyAccessService access)
{
    public async Task<(MedicationAccessResult Result, List<MedicationDto> Items)> GetForMedkitAsync(
        Guid medkitId, Guid userId, CancellationToken ct = default)
    {
        var medkit = await db.Medkits.AsNoTracking().FirstOrDefaultAsync(k => k.Id == medkitId, ct);
        if (medkit is null) return (MedicationAccessResult.NotFound, []);

        if (!await access.HasRoleAsync(userId, medkit.FamilyId, FamilyRole.Member, ct))
            return (MedicationAccessResult.Forbidden, []);

        var items = await db.Medications.AsNoTracking()
            .Where(m => m.MedkitId == medkitId)
            .Select(m => ToDto(m))
            .ToListAsync(ct);

        return (MedicationAccessResult.Success, items);
    }

    public async Task<(MedicationAccessResult Result, MedicationDto? Item)> CreateAsync(
        Guid medkitId, Guid userId, CreateMedicationRequest request, CancellationToken ct = default)
    {
        var medkit = await db.Medkits.AsNoTracking().FirstOrDefaultAsync(k => k.Id == medkitId, ct);
        if (medkit is null) return (MedicationAccessResult.NotFound, null);

        if (!await access.HasRoleAsync(userId, medkit.FamilyId, FamilyRole.Member, ct))
            return (MedicationAccessResult.Forbidden, null);

        var medication = new Medication
        {
            Id = Guid.NewGuid(),
            MedkitId = medkitId,
            FamilyId = medkit.FamilyId,
            Name = request.Name,
            Instructions = request.Instructions,
            ExpiryDate = request.ExpiryDate,
            Quantity = request.Quantity,
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow,
        };

        db.Medications.Add(medication);
        await db.SaveChangesAsync(ct);

        return (MedicationAccessResult.Success, ToDto(medication));
    }

    public async Task<MedicationAccessResult> UpdateAsync(
        Guid medicationId, Guid userId, UpdateMedicationRequest request, CancellationToken ct = default)
    {
        var medication = await db.Medications.FirstOrDefaultAsync(m => m.Id == medicationId, ct);
        if (medication is null) return MedicationAccessResult.NotFound;

        if (!await access.HasRoleAsync(userId, medication.FamilyId, FamilyRole.Member, ct))
            return MedicationAccessResult.Forbidden;

        medication.Name = request.Name;
        medication.Instructions = request.Instructions;
        medication.ExpiryDate = request.ExpiryDate;
        medication.Quantity = request.Quantity;

        await db.SaveChangesAsync(ct);
        return MedicationAccessResult.Success;
    }

    public async Task<MedicationAccessResult> DeleteAsync(Guid medicationId, Guid userId, CancellationToken ct = default)
    {
        var medication = await db.Medications.FirstOrDefaultAsync(m => m.Id == medicationId, ct);
        if (medication is null) return MedicationAccessResult.NotFound;

        if (!await access.HasRoleAsync(userId, medication.FamilyId, FamilyRole.Member, ct))
            return MedicationAccessResult.Forbidden;

        db.Medications.Remove(medication);
        await db.SaveChangesAsync(ct);
        return MedicationAccessResult.Success;
    }

    private static MedicationDto ToDto(Medication m) =>
        new(m.Id, m.MedkitId, m.FamilyId, m.Name, m.Instructions, m.ExpiryDate, m.Quantity, m.CreatedByUserId, m.CreatedAt);
}
