using FamilyHub.Domain.Enums;
using FamilyHub.Domain.ValueObjects;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.DoctorReports;

/// <summary>
/// Для кого можно составить отчёт врачу и с какими данными. Пациент — сам автор, подопечный любой его
/// активной семьи (человек или питомец) или взрослый член общей активной семьи. Записи чужого взрослого
/// берутся только из видимых автору (DoctorReportDataCollector), дневник и прививки — по гранту
/// HealthShareGrant соответствующей категории (ADR-0017). Проверка — в момент запроса: вышли из общей
/// семьи — доступа нет, отчёт создать нельзя.
/// </summary>
public class DoctorReportSubjects(AppDbContext db, IFamilyAccessService access)
{
    public async Task<List<DoctorReportSubjectDto>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        var me = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.LastName, u.FirstName, u.MiddleName }).SingleAsync(ct);
        var result = new List<DoctorReportSubjectDto>
        {
            new(DoctorReportSubjectKind.Self, null, Name(me.LastName, me.FirstName, me.MiddleName), false, true, true),
        };

        var familyIds = await access.GetActiveFamilyIdsAsync(userId, ct);
        if (familyIds.Count == 0) return result;

        var memberIds = await db.FamilyMembers.AsNoTracking()
            .Where(m => familyIds.Contains(m.FamilyId) && m.Status == MemberStatus.Active && m.UserId != userId)
            .Select(m => m.UserId).Distinct().ToListAsync(ct);
        var users = await db.Users.AsNoTracking().Where(u => memberIds.Contains(u.Id))
            .Select(u => new { u.Id, u.LastName, u.FirstName, u.MiddleName }).ToListAsync(ct);
        var grants = await db.HealthShareGrants.AsNoTracking()
            .Where(g => g.ViewerUserId == userId && memberIds.Contains(g.OwnerUserId))
            .ToDictionaryAsync(g => g.OwnerUserId, g => g.Categories, ct);

        result.AddRange(users
            .Select(u =>
            {
                var categories = grants.GetValueOrDefault(u.Id);
                return new DoctorReportSubjectDto(
                    DoctorReportSubjectKind.User, u.Id, Name(u.LastName, u.FirstName, u.MiddleName), false,
                    categories.HasFlag(HealthShareCategory.Diary), categories.HasFlag(HealthShareCategory.Vaccinations));
            })
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase));

        var dependents = await db.FamilyDependents.AsNoTracking()
            .Where(d => familyIds.Contains(d.FamilyId))
            .ToListAsync(ct);
        result.AddRange(dependents
            .Select(d => new DoctorReportSubjectDto(
                DoctorReportSubjectKind.Dependent, d.Id,
                d.IsPet ? PetName(d.FirstName, d.PetSpecies) : Name(d.LastName, d.FirstName, d.MiddleName),
                d.IsPet, false, !d.IsPet))
            .OrderBy(s => s.IsPet)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase));

        return result;
    }

    /// <summary>Пациент для сбора данных или null, если такого нет или доступа к нему у автора нет.</summary>
    public async Task<ReportSubject?> ResolveAsync(
        Guid userId, DoctorReportSubjectKind kind, Guid? subjectId, CancellationToken ct = default)
    {
        if (kind == DoctorReportSubjectKind.Self || (kind == DoctorReportSubjectKind.User && subjectId == userId))
            return ReportSubject.Self(userId);
        if (subjectId is not { } id) return null;

        var familyIds = await access.GetActiveFamilyIdsAsync(userId, ct);
        if (familyIds.Count == 0) return null;

        switch (kind)
        {
            case DoctorReportSubjectKind.Dependent:
            {
                var dep = await db.FamilyDependents.AsNoTracking()
                    .Where(d => d.Id == id && familyIds.Contains(d.FamilyId))
                    .Select(d => new { d.IsPet })
                    .FirstOrDefaultAsync(ct);
                return dep is null ? null : new ReportSubject(userId, null, id, false, !dep.IsPet);
            }
            case DoctorReportSubjectKind.User:
            {
                var shareFamily = await db.FamilyMembers.AsNoTracking()
                    .AnyAsync(m => m.UserId == id && m.Status == MemberStatus.Active && familyIds.Contains(m.FamilyId), ct);
                if (!shareFamily) return null;
                var categories = await db.HealthShareGrants.AsNoTracking()
                    .Where(g => g.OwnerUserId == id && g.ViewerUserId == userId)
                    .Select(g => g.Categories)
                    .FirstOrDefaultAsync(ct);
                return new ReportSubject(userId, id, null,
                    categories.HasFlag(HealthShareCategory.Diary), categories.HasFlag(HealthShareCategory.Vaccinations));
            }
            default:
                return null;
        }
    }

    private static string Name(string? lastName, string? firstName, string? middleName)
    {
        var name = PersonName.Format(lastName, firstName, middleName, PersonNameStyle.Full).Trim();
        return string.IsNullOrEmpty(name) ? "Без имени" : name;
    }

    private static string PetName(string firstName, string? species) =>
        string.IsNullOrWhiteSpace(species) ? firstName.Trim() : $"{firstName.Trim()} ({species.Trim()})";
}
