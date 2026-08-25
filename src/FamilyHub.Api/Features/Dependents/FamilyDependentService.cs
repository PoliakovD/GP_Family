using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Api.Features.Dependents;

/// <summary>
/// Подопечные (дети/питомцы/пожилые родственники без своего User) — семейный ресурс, живёт в
/// Core.Families (не в Modules.Medical: это концепция семьи, не медицины), рядом с FamilyService/
/// MembershipService/InviteService. Create/Update — любой активный Member (как Medkit/Birthday);
/// Delete — только Admin (осознанное отличие от остальных семейных ресурсов) с каскадным
/// удалением связанных MedicalRecord и физической чисткой их вложений из MinIO.
/// </summary>
public class FamilyDependentService(
    AppDbContext db, IFamilyAccessService access, IFileStorage storage, ILogger<FamilyDependentService> logger)
{
    public async Task<(FamilyDependentAccessResult Result, List<FamilyDependentDto> Items)> GetForFamilyAsync(
        Guid familyId, Guid userId, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(userId, familyId, FamilyRole.Member, ct))
        {
            logger.LogWarning("Список подопечных отклонён: {UserId} не состоит в семье {FamilyId}", userId, familyId);
            return (FamilyDependentAccessResult.Forbidden, []);
        }

        // Материализуем сущности, потом мапим в DTO в памяти (не Select-проекция в SQL) — Name
        // зашифровано, тот же приём, что MedicalRecordService.GetVisibleRecordsAsync.
        var dependents = await db.FamilyDependents.AsNoTracking()
            .Where(d => d.FamilyId == familyId)
            .ToListAsync(ct);

        return (FamilyDependentAccessResult.Success, dependents.Select(ToDto).ToList());
    }

    public async Task<(FamilyDependentAccessResult Result, FamilyDependentDto? Item)> CreateAsync(
        Guid familyId, Guid userId, CreateFamilyDependentRequest request, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(userId, familyId, FamilyRole.Member, ct))
        {
            logger.LogWarning("Создание подопечного отклонено: {UserId} не состоит в семье {FamilyId}", userId, familyId);
            return (FamilyDependentAccessResult.Forbidden, null);
        }

        var dependent = new FamilyDependent
        {
            Id = Guid.NewGuid(),
            FamilyId = familyId,
            FirstName = request.FirstName,
            // ФИО/PetSpecies — сервис не доверяет фронту, зануляет несовместимое с IsPet поле
            // явно (тот же принцип, что раньше применялся только к PetSpecies).
            LastName = request.IsPet ? null : request.LastName?.Trim(),
            MiddleName = request.IsPet ? null : request.MiddleName?.Trim(),
            Gender = request.Gender,
            BirthDate = request.BirthDate,
            IsPet = request.IsPet,
            PetSpecies = request.IsPet ? request.PetSpecies?.Trim() : null,
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow,
        };

        db.FamilyDependents.Add(dependent);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Подопечный {DependentId} создан пользователем {UserId} в семье {FamilyId}", dependent.Id, userId, familyId);
        return (FamilyDependentAccessResult.Success, ToDto(dependent));
    }

    public async Task<FamilyDependentAccessResult> UpdateAsync(
        Guid dependentId, Guid userId, UpdateFamilyDependentRequest request, CancellationToken ct = default)
    {
        var dependent = await db.FamilyDependents.FirstOrDefaultAsync(d => d.Id == dependentId, ct);
        if (dependent is null)
        {
            logger.LogWarning("Обновление подопечного {DependentId}: не найден (запросил {UserId})", dependentId, userId);
            return FamilyDependentAccessResult.NotFound;
        }

        if (!await access.HasRoleAsync(userId, dependent.FamilyId, FamilyRole.Member, ct))
        {
            logger.LogWarning(
                "Обновление подопечного {DependentId} отклонено: {UserId} не состоит в семье {FamilyId}",
                dependentId, userId, dependent.FamilyId);
            return FamilyDependentAccessResult.Forbidden;
        }

        dependent.FirstName = request.FirstName;
        dependent.LastName = request.IsPet ? null : request.LastName?.Trim();
        dependent.MiddleName = request.IsPet ? null : request.MiddleName?.Trim();
        dependent.Gender = request.Gender;
        dependent.BirthDate = request.BirthDate;
        dependent.IsPet = request.IsPet;
        dependent.PetSpecies = request.IsPet ? request.PetSpecies?.Trim() : null;

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Подопечный {DependentId} обновлён пользователем {UserId}", dependentId, userId);
        return FamilyDependentAccessResult.Success;
    }

    /// <summary>
    /// Только Admin семьи. Каскад: связанные MedicalRecord удаляются вместе с ним (FK
    /// DELETE CASCADE — см. MedicalRecordConfiguration), но их FileAttachment-строки и MinIO-блобы
    /// FK-less и требуют явной чистки — тот же паттерн, что AccountService.DeleteAccountAsync:
    /// собрать ключи ДО удаления → транзакция → коммит → best-effort удаление блобов.
    /// </summary>
    public async Task<FamilyDependentAccessResult> DeleteAsync(Guid dependentId, Guid userId, CancellationToken ct = default)
    {
        var dependent = await db.FamilyDependents.FirstOrDefaultAsync(d => d.Id == dependentId, ct);
        if (dependent is null)
        {
            logger.LogWarning("Удаление подопечного {DependentId}: не найден (запросил {UserId})", dependentId, userId);
            return FamilyDependentAccessResult.NotFound;
        }

        if (!await access.HasRoleAsync(userId, dependent.FamilyId, FamilyRole.Admin, ct))
        {
            logger.LogWarning(
                "Удаление подопечного {DependentId} отклонено: {UserId} не админ семьи {FamilyId}",
                dependentId, userId, dependent.FamilyId);
            return FamilyDependentAccessResult.Forbidden;
        }

        var recordIds = await db.MedicalRecords.Where(r => r.FamilyDependentId == dependentId)
            .Select(r => r.Id).ToListAsync(ct);
        var storageKeys = await db.FileAttachments
            .Where(a => a.OwnerType == FileOwnerType.MedicalRecord && recordIds.Contains(a.OwnerId))
            .Select(a => a.StorageKey)
            .ToListAsync(ct);

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.FileAttachments
                .Where(a => a.OwnerType == FileOwnerType.MedicalRecord && recordIds.Contains(a.OwnerId))
                .ExecuteDeleteAsync(ct);
            // MedicalRecordHidden по этим записям — каскадом FK (MedicalRecordHiddenConfiguration).
            await db.MedicalRecords.Where(r => r.FamilyDependentId == dependentId).ExecuteDeleteAsync(ct);
            await db.FamilyDependents.Where(d => d.Id == dependentId).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        }

        // Файлы — после коммита БД: сбой удаления объекта не откатывает удаление подопечного,
        // осиротевший шифрованный блоб нечитаем без строки FileAttachment и ключа.
        foreach (var key in storageKeys)
        {
            try
            {
                await storage.DeleteAsync(key, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Удаление подопечного {DependentId}: не удалось удалить блоб {StorageKey}", dependentId, key);
            }
        }

        logger.LogInformation(
            "Подопечный {DependentId} удалён пользователем {UserId} ({Records} записей, {Files} файлов)",
            dependentId, userId, recordIds.Count, storageKeys.Count);
        return FamilyDependentAccessResult.Success;
    }

    private static FamilyDependentDto ToDto(FamilyDependent d) =>
        new(d.Id, d.FamilyId, d.FirstName, d.LastName, d.MiddleName, d.Gender,
            d.BirthDate, d.IsPet, d.PetSpecies, d.CreatedByUserId, d.CreatedAt);
}
