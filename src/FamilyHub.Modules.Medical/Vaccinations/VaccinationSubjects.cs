using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.Vaccinations;

/// <summary>Человек, чей график прививок можно посмотреть — сам пользователь или подопечный семьи
/// (питомцы исключены: национальный календарь — для людей). BirthDate/Gender нужны калькулятору
/// графика; их отсутствие (профиль/карточка ещё не заполнены) означает «график не построить».</summary>
public record VaccinationSubjectInfo(
    string Kind, Guid Id, string Name, bool IsSelf, DateOnly? BirthDate, Gender? Gender, Guid? FamilyId, bool CanEdit);

/// <summary>Подписи и разрешение людей для прививок — параллель CourseSubjects у курсов приёма, но с
/// датой рождения/полом (нужны калькулятору) и явным списком «кто мне вообще виден» для обзора.</summary>
public class VaccinationSubjects(AppDbContext db)
{
    public const string UserKind = "user";
    public const string DependentKind = "dependent";
    private const string UnknownName = "Без имени";

    public async Task<VaccinationSubjectInfo?> FindAsync(
        Guid viewerId, string kind, Guid id, VaccinationScope scope, CancellationToken ct = default)
    {
        if (kind == UserKind)
        {
            var user = await db.Users.AsNoTracking().Where(u => u.Id == id)
                .Select(u => new { u.Id, u.FirstName, u.Username, u.BirthDate, u.Gender }).FirstOrDefaultAsync(ct);
            if (user is null) return null;
            var access = scope.AccessTo(id, null, null);
            if (access == VaccinationAccessLevel.None) return null;
            return new VaccinationSubjectInfo(
                UserKind, user.Id, user.Id == viewerId ? "Я" : FirstNonEmpty(user.FirstName, user.Username),
                user.Id == viewerId, user.BirthDate, user.Gender, null, access == VaccinationAccessLevel.Full);
        }

        if (kind == DependentKind)
        {
            var dep = await db.FamilyDependents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
            if (dep is null || dep.IsPet) return null;
            var access = scope.AccessTo(null, dep.Id, dep.FamilyId);
            if (access == VaccinationAccessLevel.None) return null;
            return new VaccinationSubjectInfo(
                DependentKind, dep.Id, FirstNonEmpty(dep.FirstName), false, dep.BirthDate, dep.Gender, dep.FamilyId,
                access == VaccinationAccessLevel.Full);
        }

        return null;
    }

    /// <summary>Все люди, чьи прививки видит пользователь: сам + не-питомцы подопечные активных
    /// семей + наблюдаемые взрослые — для обзора «Семья» и модалки «Кому».</summary>
    public async Task<List<VaccinationSubjectInfo>> ListVisibleAsync(
        Guid viewerId, VaccinationScope scope, CancellationToken ct = default)
    {
        var result = new List<VaccinationSubjectInfo>();

        var me = await db.Users.AsNoTracking().Where(u => u.Id == viewerId)
            .Select(u => new { u.Id, u.BirthDate, u.Gender }).FirstOrDefaultAsync(ct);
        if (me is not null)
            result.Add(new VaccinationSubjectInfo(UserKind, me.Id, "Я", true, me.BirthDate, me.Gender, null, true));

        if (scope.FamilyIds.Count > 0)
        {
            var dependents = await db.FamilyDependents.AsNoTracking()
                .Where(d => scope.FamilyIds.Contains(d.FamilyId) && !d.IsPet)
                .ToListAsync(ct);
            result.AddRange(dependents.Select(d =>
                new VaccinationSubjectInfo(DependentKind, d.Id, FirstNonEmpty(d.FirstName), false, d.BirthDate, d.Gender, d.FamilyId, true)));
        }

        if (scope.WatchedUserIds.Count > 0)
        {
            var watched = await db.Users.AsNoTracking().Where(u => scope.WatchedUserIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FirstName, u.Username, u.BirthDate, u.Gender }).ToListAsync(ct);
            result.AddRange(watched.Select(u =>
                new VaccinationSubjectInfo(UserKind, u.Id, FirstNonEmpty(u.FirstName, u.Username), false, u.BirthDate, u.Gender, null, false)));
        }

        return result;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? UnknownName;
}
