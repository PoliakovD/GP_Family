using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.MedicationCourses;

/// <summary>Подписи людей для курсов: имя пользователя — из профиля, подопечного — из зашифрованной
/// карточки (расшифровывает EF-конвертер). Только имя без фамилии: этого хватает для чипов и
/// уведомлений, и не тащит лишние ПДн в ответы и push.</summary>
public class CourseSubjects(AppDbContext db)
{
    public const string UserKind = "user";
    public const string DependentKind = "dependent";
    public const string UnknownName = "Без имени";

    public async Task<Dictionary<Guid, SubjectDto>> ResolveAsync(
        IEnumerable<MedicationCourse> courses, Guid viewerId, CancellationToken ct = default)
    {
        var list = courses.ToList();
        var result = new Dictionary<Guid, SubjectDto>();

        var userIds = list.Where(c => c.SubjectUserId != null).Select(c => c.SubjectUserId!.Value).Distinct().ToList();
        if (userIds.Count > 0)
        {
            var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FirstName, u.Username }).ToListAsync(ct);
            foreach (var u in users)
                result[u.Id] = new SubjectDto(UserKind, u.Id, FirstNonEmpty(u.FirstName, u.Username), u.Id == viewerId);
        }

        var dependentIds = list.Where(c => c.FamilyDependentId != null).Select(c => c.FamilyDependentId!.Value).Distinct().ToList();
        if (dependentIds.Count > 0)
        {
            // FirstName зашифрован — читаем сущности целиком, чтобы сработал конвертер.
            var dependents = await db.FamilyDependents.AsNoTracking().Where(d => dependentIds.Contains(d.Id)).ToListAsync(ct);
            foreach (var d in dependents)
                result[d.Id] = new SubjectDto(DependentKind, d.Id, FirstNonEmpty(d.FirstName), false);
        }

        return result;
    }

    public static SubjectDto? Of(IReadOnlyDictionary<Guid, SubjectDto> subjects, MedicationCourse c) =>
        subjects.GetValueOrDefault(c.SubjectUserId ?? c.FamilyDependentId ?? Guid.Empty);

    /// <summary>Имена пользователей по Id (для наблюдателей и кандидатов).</summary>
    public async Task<Dictionary<Guid, string>> UserNamesAsync(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.FirstName, u.Username }).ToListAsync(ct);
        return users.ToDictionary(u => u.Id, u => FirstNonEmpty(u.FirstName, u.Username));
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? UnknownName;
}
