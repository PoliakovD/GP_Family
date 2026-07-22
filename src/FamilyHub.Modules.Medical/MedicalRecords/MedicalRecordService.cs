using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Outbox;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.MedicalRecords;

/// <summary>
/// Мед-анализы — персональный ресурс (раздел 4.2 брифа): принадлежат пользователю, НЕ
/// семье, приватны по умолчанию. Шарингом и скрытием управляет ТОЛЬКО владелец — даже
/// админ семьи сюда не лезет (инвариант 2). Видимость — дословно по разделу 6 брифа.
/// </summary>
public class MedicalRecordService(AppDbContext db, IFamilyAccessService access, IOutboxWriter outbox, ILogger<MedicalRecordService> logger)
{
    /// <summary>
    /// Видно, если: владелец, ИЛИ (мои анализы расшарены этой семье И я в ней состою
    /// активным членом И запись не скрыта именно от неё). Главный запрос раздела 6.
    /// </summary>
    private IQueryable<MedicalRecord> VisibleRecordsQuery(Guid userId) =>
        db.MedicalRecords.AsNoTracking().Where(r =>
            r.OwnerUserId == userId
            || db.FamilyMedicalShares.Any(share =>
                   share.OwnerUserId == r.OwnerUserId &&
                   db.FamilyMembers.Any(m =>
                       m.FamilyId == share.FamilyId &&
                       m.UserId == userId &&
                       m.Status == MemberStatus.Active) &&
                   !db.MedicalRecordHiddens.Any(h =>
                       h.MedicalRecordId == r.Id &&
                       h.FamilyId == share.FamilyId)));

    /// <summary>
    /// HiddenFamilyIds (L2) отдаётся только владельцу записи — это его личная настройка доступа,
    /// а не то, что должны видеть другие члены семьи, которым запись расшарена.
    /// </summary>
    public async Task<List<MedicalRecordDto>> GetVisibleRecordsAsync(Guid userId, CancellationToken ct = default)
    {
        var records = await VisibleRecordsQuery(userId).ToListAsync(ct);

        var ownRecordIds = records.Where(r => r.OwnerUserId == userId).Select(r => r.Id).ToList();
        var hiddenRows = await db.MedicalRecordHiddens
            .Where(h => ownRecordIds.Contains(h.MedicalRecordId))
            .ToListAsync(ct);
        var hiddenByRecord = hiddenRows
            .GroupBy(h => h.MedicalRecordId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(h => h.FamilyId).ToList());

        return records
            .Select(r => ToDto(
                r,
                r.OwnerUserId == userId && hiddenByRecord.TryGetValue(r.Id, out var ids) ? ids : []))
            .ToList();
    }

    public Task<bool> IsVisibleToAsync(Guid recordId, Guid userId, CancellationToken ct = default) =>
        VisibleRecordsQuery(userId).AnyAsync(r => r.Id == recordId, ct);

    /// <summary>УРОВЕНЬ 1 (чтение): семьи, которым владелец глобально расшарил свои записи.</summary>
    public Task<List<Guid>> GetSharedFamilyIdsAsync(Guid ownerUserId, CancellationToken ct = default) =>
        db.FamilyMedicalShares.AsNoTracking()
            .Where(s => s.OwnerUserId == ownerUserId)
            .Select(s => s.FamilyId)
            .ToListAsync(ct);

    public async Task<MedicalRecordDto> CreateAsync(Guid ownerUserId, CreateMedicalRecordRequest request, CancellationToken ct = default)
    {
        var record = new MedicalRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            PersonName = request.PersonName,
            RecordDate = request.RecordDate,
            Doctor = request.Doctor,
            Description = request.Description,
            CreatedAt = DateTime.UtcNow,
        };
        db.MedicalRecords.Add(record);

        List<Guid> hiddenFamilyIds = [];
        if (request.HideFromFamilyIds is { Count: > 0 })
        {
            // Инвариант 4: разрешены только семьи из пересечения «мои семьи» ∩ «расшаренные».
            var sharedFamilyIds = await db.FamilyMedicalShares
                .Where(s => s.OwnerUserId == ownerUserId)
                .Select(s => s.FamilyId)
                .ToListAsync(ct);
            var myFamilyIds = await access.GetActiveFamilyIdsAsync(ownerUserId, ct);
            hiddenFamilyIds = request.HideFromFamilyIds.Intersect(sharedFamilyIds).Intersect(myFamilyIds).ToList();

            foreach (var familyId in hiddenFamilyIds)
                db.MedicalRecordHiddens.Add(new MedicalRecordHidden
                {
                    Id = Guid.NewGuid(),
                    MedicalRecordId = record.Id,
                    FamilyId = familyId,
                    HiddenAt = DateTime.UtcNow,
                });
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Мед-запись {RecordId} создана владельцем {OwnerUserId}", record.Id, ownerUserId);
        return ToDto(record, hiddenFamilyIds);
    }

    /// <summary>УРОВЕНЬ 1: владелец открывает ВСЕ свои анализы выбранной семье одним действием.</summary>
    public async Task<MedicalRecordAccessResult> ShareWithFamilyAsync(Guid ownerUserId, Guid familyId, CancellationToken ct = default)
    {
        // Расшарить можно только семье, в которой сам состоишь.
        if (!await access.HasRoleAsync(ownerUserId, familyId, FamilyRole.Member, ct))
        {
            logger.LogWarning(
                "Шаринг мед-записей отклонён: {UserId} не состоит в семье {FamilyId}", ownerUserId, familyId);
            return MedicalRecordAccessResult.Forbidden;
        }

        var exists = await db.FamilyMedicalShares.AnyAsync(
            s => s.OwnerUserId == ownerUserId && s.FamilyId == familyId, ct);
        if (!exists)
        {
            db.FamilyMedicalShares.Add(new FamilyMedicalShare
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                FamilyId = familyId,
                SharedAt = DateTime.UtcNow,
            });
            // Только при реально созданной шаре (повторный вызов события не порождает).
            outbox.Enqueue(new MedicalRecordSharedEvent(familyId, ownerUserId));
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Пользователь {OwnerUserId} расшарил мед-записи семье {FamilyId}", ownerUserId, familyId);
        }

        return MedicalRecordAccessResult.Success;
    }

    /// <summary>
    /// Отключает шаринг семье. MedicalRecordHidden НЕ чистим (инвариант 5) — при повторном
    /// включении точечно скрытое останется скрытым.
    /// </summary>
    public async Task<MedicalRecordAccessResult> UnshareFamilyAsync(Guid ownerUserId, Guid familyId, CancellationToken ct = default)
    {
        var share = await db.FamilyMedicalShares.FirstOrDefaultAsync(
            s => s.OwnerUserId == ownerUserId && s.FamilyId == familyId, ct);
        if (share is null)
        {
            logger.LogWarning(
                "Отмена шаринга: шаринг {OwnerUserId} -> {FamilyId} не найден", ownerUserId, familyId);
            return MedicalRecordAccessResult.NotFound;
        }

        db.FamilyMedicalShares.Remove(share);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Пользователь {OwnerUserId} отменил шаринг мед-записей семье {FamilyId}", ownerUserId, familyId);
        return MedicalRecordAccessResult.Success;
    }

    /// <summary>УРОВЕНЬ 2: точечно скрыть запись от выбранных семей (из числа уже расшаренных).</summary>
    public async Task<MedicalRecordAccessResult> HideFromFamiliesAsync(
        Guid ownerUserId, Guid recordId, List<Guid> familyIds, CancellationToken ct = default)
    {
        var record = await db.MedicalRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recordId, ct);
        if (record is null)
        {
            logger.LogWarning("Скрытие мед-записи {RecordId}: не найдена (запросил {UserId})", recordId, ownerUserId);
            return MedicalRecordAccessResult.NotFound;
        }

        // Инвариант 2: шарингом и скрытием управляет ТОЛЬКО владелец, даже админ семьи не может.
        if (record.OwnerUserId != ownerUserId)
        {
            logger.LogWarning(
                "Скрытие мед-записи {RecordId} отклонено: {UserId} не владелец", recordId, ownerUserId);
            return MedicalRecordAccessResult.Forbidden;
        }

        var sharedFamilyIds = await db.FamilyMedicalShares
            .Where(s => s.OwnerUserId == ownerUserId && familyIds.Contains(s.FamilyId))
            .Select(s => s.FamilyId)
            .ToListAsync(ct);

        foreach (var familyId in sharedFamilyIds)
        {
            var alreadyHidden = await db.MedicalRecordHiddens.AnyAsync(
                h => h.MedicalRecordId == recordId && h.FamilyId == familyId, ct);
            if (!alreadyHidden)
                db.MedicalRecordHiddens.Add(new MedicalRecordHidden
                {
                    Id = Guid.NewGuid(),
                    MedicalRecordId = recordId,
                    FamilyId = familyId,
                    HiddenAt = DateTime.UtcNow,
                });
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Мед-запись {RecordId} скрыта от семей [{FamilyIds}] владельцем {UserId}",
            recordId, string.Join(',', sharedFamilyIds), ownerUserId);
        return MedicalRecordAccessResult.Success;
    }

    public async Task<MedicalRecordAccessResult> UnhideFromFamiliesAsync(
        Guid ownerUserId, Guid recordId, List<Guid> familyIds, CancellationToken ct = default)
    {
        var record = await db.MedicalRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recordId, ct);
        if (record is null)
        {
            logger.LogWarning("Раскрытие мед-записи {RecordId}: не найдена (запросил {UserId})", recordId, ownerUserId);
            return MedicalRecordAccessResult.NotFound;
        }
        if (record.OwnerUserId != ownerUserId)
        {
            logger.LogWarning(
                "Раскрытие мед-записи {RecordId} отклонено: {UserId} не владелец", recordId, ownerUserId);
            return MedicalRecordAccessResult.Forbidden;
        }

        var hidden = db.MedicalRecordHiddens.Where(h => h.MedicalRecordId == recordId && familyIds.Contains(h.FamilyId));
        db.MedicalRecordHiddens.RemoveRange(hidden);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Мед-запись {RecordId} раскрыта семьям [{FamilyIds}] владельцем {UserId}",
            recordId, string.Join(',', familyIds), ownerUserId);
        return MedicalRecordAccessResult.Success;
    }

    private static MedicalRecordDto ToDto(MedicalRecord r, IReadOnlyList<Guid> hiddenFamilyIds) =>
        new(r.Id, r.OwnerUserId, r.PersonName, r.RecordDate, r.Doctor, r.Description, r.CreatedAt, hiddenFamilyIds);
}
