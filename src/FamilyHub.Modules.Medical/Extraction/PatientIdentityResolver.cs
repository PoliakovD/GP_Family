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
}
