using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.MedicationCourses;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Access;

public enum HealthShareResult { Success, Invalid }

/// <summary>Кому я дал доступ и сколько категорий у него открыто.</summary>
public record HealthShareGrantDto(Guid ViewerUserId, string Name, HealthShareCategory Categories);

/// <summary>Чьё здоровье вижу я и что именно.</summary>
public record HealthSharedWithMeDto(Guid OwnerUserId, string Name, HealthShareCategory Categories);

public record SetHealthShareRequest(HealthShareCategory Categories);

/// <summary>
/// Управление грантами доступа per-категория (ADR-0017) — «Кто видит моё здоровье» в настройках.
/// Единственный источник истины о read-доступе к чужому дневнику/приёму/прививкам:
/// SubjectScopeService читает эту же таблицу. Кандидаты — активные члены МОИХ активных семей, тот же
/// круг, что и у «моих наблюдателей» в MedicationReminderSettingsService (историческая параллельная
/// таблица MedicationWatcher отвечает после этой фичи только за уведомления, не за доступ).
/// </summary>
public class HealthShareService(AppDbContext db, IFamilyAccessService access, CourseSubjects subjects, ILogger<HealthShareService> logger)
{
    /// <summary>Полная матрица: каждый активный член моих семей + его текущие категории (None, если
    /// доступа нет). GET всегда возвращает весь список кандидатов, не только тех, кому что-то дано —
    /// экран настроек рисует переключатели для всех сразу (тот же приём, что sparse-preferences,
    /// см. .claude/patterns/backend.md).</summary>
    public async Task<List<HealthShareGrantDto>> GetMineAsync(Guid userId, CancellationToken ct = default)
    {
        var candidateIds = await ActiveOtherMemberIdsAsync(userId, ct);
        if (candidateIds.Count == 0) return [];

        var names = await subjects.UserNamesAsync(candidateIds, ct);
        var granted = await db.HealthShareGrants.AsNoTracking()
            .Where(g => g.OwnerUserId == userId && candidateIds.Contains(g.ViewerUserId))
            .ToDictionaryAsync(g => g.ViewerUserId, g => g.Categories, ct);

        return candidateIds
            .Select(id => new HealthShareGrantDto(id, names.GetValueOrDefault(id, CourseSubjects.UnknownName), granted.GetValueOrDefault(id, HealthShareCategory.None)))
            .OrderBy(g => g.Name)
            .ToList();
    }

    /// <summary>Заменяет набор категорий, открытых одному зрителю. None удаляет строку целиком —
    /// таблица не хранит грант «ничего не открыто» (см. class doc HealthShareGrant).</summary>
    public async Task<HealthShareResult> SetAsync(Guid userId, Guid viewerUserId, HealthShareCategory categories, CancellationToken ct = default)
    {
        if (viewerUserId == userId) return HealthShareResult.Invalid;

        var candidateIds = await ActiveOtherMemberIdsAsync(userId, ct);
        if (!candidateIds.Contains(viewerUserId)) return HealthShareResult.Invalid;

        var row = await db.HealthShareGrants.FirstOrDefaultAsync(g => g.OwnerUserId == userId && g.ViewerUserId == viewerUserId, ct);
        var now = DateTime.UtcNow;

        if (categories == HealthShareCategory.None)
        {
            if (row is not null) db.HealthShareGrants.Remove(row);
        }
        else if (row is null)
        {
            db.HealthShareGrants.Add(new HealthShareGrant
            {
                Id = Guid.NewGuid(), OwnerUserId = userId, ViewerUserId = viewerUserId,
                Categories = categories, CreatedAt = now, UpdatedAt = now,
            });
        }
        else
        {
            row.Categories = categories;
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Доступ к здоровью {OwnerId} → {ViewerId} изменён на {Categories}", userId, viewerUserId, categories);
        return HealthShareResult.Success;
    }

    /// <summary>Добавляет категорию к существующему гранту (или создаёт его), не трогая остальные биты —
    /// используется при включении наблюдателя за приёмом лекарств (ADR-0015 UI), чтобы то же действие
    /// заодно открывало доступ на чтение курсов. Кандидатность не проверяется — вызывающий её уже
    /// проверил своей собственной (более узкой для курсов) логикой.</summary>
    public async Task GrantCategoryAsync(Guid ownerUserId, Guid viewerUserId, HealthShareCategory category, CancellationToken ct = default)
    {
        var row = await db.HealthShareGrants.FirstOrDefaultAsync(g => g.OwnerUserId == ownerUserId && g.ViewerUserId == viewerUserId, ct);
        var now = DateTime.UtcNow;
        if (row is null)
        {
            db.HealthShareGrants.Add(new HealthShareGrant
            {
                Id = Guid.NewGuid(), OwnerUserId = ownerUserId, ViewerUserId = viewerUserId,
                Categories = category, CreatedAt = now, UpdatedAt = now,
            });
        }
        else if ((row.Categories & category) != category)
        {
            row.Categories |= category;
            row.UpdatedAt = now;
        }
        else
        {
            return;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Чьё здоровье вижу я — только пока с владельцем есть общая активная семья (как и у
    /// самого чтения, см. SubjectScopeService); выданный, но «протухший» после выхода из семьи грант
    /// сюда не попадает, хотя строка в таблице ещё лежит.</summary>
    public async Task<List<HealthSharedWithMeDto>> GetSharedWithMeAsync(Guid userId, CancellationToken ct = default)
    {
        var familyIds = await access.GetActiveFamilyIdsAsync(userId, ct);
        if (familyIds.Count == 0) return [];

        var grants = await db.HealthShareGrants.AsNoTracking()
            .Where(g => g.ViewerUserId == userId && g.Categories != HealthShareCategory.None)
            .ToListAsync(ct);
        if (grants.Count == 0) return [];

        var ownerIds = grants.Select(g => g.OwnerUserId).Distinct().ToList();
        var sharedFamilyOwnerIds = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.Status == MemberStatus.Active && familyIds.Contains(m.FamilyId) && ownerIds.Contains(m.UserId))
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync(ct);
        if (sharedFamilyOwnerIds.Count == 0) return [];

        var names = await subjects.UserNamesAsync(sharedFamilyOwnerIds, ct);
        return grants
            .Where(g => sharedFamilyOwnerIds.Contains(g.OwnerUserId))
            .Select(g => new HealthSharedWithMeDto(g.OwnerUserId, names.GetValueOrDefault(g.OwnerUserId, CourseSubjects.UnknownName), g.Categories))
            .OrderBy(g => g.Name)
            .ToList();
    }

    private async Task<List<Guid>> ActiveOtherMemberIdsAsync(Guid userId, CancellationToken ct)
    {
        var familyIds = await access.GetActiveFamilyIdsAsync(userId, ct);
        if (familyIds.Count == 0) return [];
        return await db.FamilyMembers.AsNoTracking()
            .Where(m => familyIds.Contains(m.FamilyId) && m.Status == MemberStatus.Active && m.UserId != userId)
            .Select(m => m.UserId).Distinct().ToListAsync(ct);
    }
}
