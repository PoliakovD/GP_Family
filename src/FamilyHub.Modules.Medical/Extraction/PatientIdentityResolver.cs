using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Возраст и пол пациента мед-записи (identity rework) — общий для
/// MedicalDocumentExtractionProcessor (свежее распознавание) и RecalculateIndicatorFlagsJob
/// (дозаполнение флагов задним числом). Для FamilyDependent оба берутся оттуда (Gender там
/// required, всегда есть); для "себя"/TargetUserId — из профиля владельца/назначенного участника
/// (оба поля nullable — деградация до null при незаполненном профиле).
/// </summary>
public static class PatientIdentityResolver
{
    public static async Task<(int? AgeYears, Gender? Sex)> ResolveAsync(
        AppDbContext db, Domain.Entities.MedicalRecord record, CancellationToken ct = default)
    {
        if (record.FamilyDependentId is { } depId)
        {
            var dep = await db.FamilyDependents.AsNoTracking()
                .Where(d => d.Id == depId).Select(d => new { d.BirthDate, d.Gender }).FirstOrDefaultAsync(ct);
            if (dep is null) return (null, null);
            return (CalculateAge(dep.BirthDate, record.RecordDate), dep.Gender);
        }

        var userId = record.TargetUserId ?? record.OwnerUserId;
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId).Select(u => new { u.BirthDate, u.Gender }).FirstOrDefaultAsync(ct);
        if (user is null) return (null, null);
        return (CalculateAge(user.BirthDate, record.RecordDate), user.Gender);
    }

    private static int? CalculateAge(DateOnly? birthDate, DateOnly asOf)
    {
        if (birthDate is null) return null;
        var age = asOf.Year - birthDate.Value.Year;
        if (asOf < birthDate.Value.AddYears(age)) age--;
        return age >= 0 ? age : null;
    }

    /// <summary>Батч-резолв отображаемого имени пациента по идентичности (FamilyDependentId,
    /// TargetUserId, OwnerUserId) — та же формула, что MedicalRecordService.ResolvePersonNamesAsync,
    /// но ключ — сама идентичность пациента, не Id конкретной записи: нужно там, где под рукой нет
    /// MedicalRecord целиком, а есть только денормализованная идентичность (LabIndicator, см.
    /// ExtractionQueryService.GetMyIndicatorsAsync — "мои показатели" по нескольким пациентам).</summary>
    public static async Task<Dictionary<(Guid? FamilyDependentId, Guid? TargetUserId), string>> ResolvePatientNamesAsync(
        AppDbContext db, IEnumerable<(Guid? FamilyDependentId, Guid? TargetUserId, Guid OwnerUserId)> patients, CancellationToken ct = default)
    {
        var distinct = patients.Distinct().ToList();
        var dependentIds = distinct.Where(p => p.FamilyDependentId is not null)
            .Select(p => p.FamilyDependentId!.Value).Distinct().ToList();
        var userIds = distinct.Where(p => p.FamilyDependentId is null)
            .Select(p => p.TargetUserId ?? p.OwnerUserId).Distinct().ToList();

        var dependentNames = dependentIds.Count == 0
            ? new Dictionary<Guid, string>()
            : (await db.FamilyDependents.AsNoTracking().Where(d => dependentIds.Contains(d.Id)).ToListAsync(ct))
                .ToDictionary(d => d.Id, d => MedicalRecords.MedicalRecordService.FormatName(d.FirstName, d.LastName, null));

        var userNames = userIds.Count == 0
            ? new Dictionary<Guid, string>()
            : (await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToListAsync(ct))
                .ToDictionary(u => u.Id, u => MedicalRecords.MedicalRecordService.FormatName(u.FirstName, u.LastName, u.MiddleName));

        var result = new Dictionary<(Guid?, Guid?), string>();
        foreach (var p in distinct)
        {
            if (p.FamilyDependentId is { } depId)
            {
                result[(p.FamilyDependentId, p.TargetUserId)] = dependentNames.TryGetValue(depId, out var dn) ? dn : "Без имени";
                continue;
            }

            var uid = p.TargetUserId ?? p.OwnerUserId;
            var name = userNames.TryGetValue(uid, out var un) ? un : string.Empty;
            result[(p.FamilyDependentId, p.TargetUserId)] = name.Length > 0 ? name : (p.TargetUserId is null ? "Я" : "Без имени");
        }
        return result;
    }
}
