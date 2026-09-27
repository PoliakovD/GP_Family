using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Access;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.Vaccinations;

/// <summary>Уровень доступа к прививкам человека: Watch — только чтение («вы следите»),
/// Full — просмотр, отметки, файлы.</summary>
public enum VaccinationAccessLevel { None = 0, Watch = 1, Full = 2 }

/// <summary>
/// Кто чьи прививки видит — тот же трёхканальный принцип, что у курсов приёма лекарств (ADR-0015):
/// свои прививки; прививки подопечного семьи, где я активный член (полный доступ — как и с самим
/// подопечным); прививки взрослого члена семьи, который выбрал меня наблюдателем и с которым
/// по-прежнему есть общая активная семья (только чтение). Данные скоупа — общие с курсами,
/// см. <see cref="SubjectScopeService"/>.
/// </summary>
public class VaccinationScope(
    Guid userId, IReadOnlyCollection<Guid> familyIds, IReadOnlyCollection<Guid> adminFamilyIds,
    IReadOnlyCollection<Guid> watchedUserIds)
{
    public Guid UserId { get; } = userId;
    public IReadOnlyCollection<Guid> FamilyIds { get; } = familyIds;
    public IReadOnlyCollection<Guid> AdminFamilyIds { get; } = adminFamilyIds;
    public IReadOnlyCollection<Guid> WatchedUserIds { get; } = watchedUserIds;

    public IQueryable<Vaccination> Apply(IQueryable<Vaccination> query)
    {
        var uid = UserId;
        var families = FamilyIds.ToList();
        var watched = WatchedUserIds.ToList();
        return query.Where(v =>
            v.SubjectUserId == uid
            || (v.FamilyDependentId != null && v.FamilyId != null && families.Contains(v.FamilyId.Value))
            || (v.SubjectUserId != null && watched.Contains(v.SubjectUserId.Value)));
    }

    public IQueryable<VaccinationCertificate> ApplyCertificates(IQueryable<VaccinationCertificate> query)
    {
        var uid = UserId;
        var families = FamilyIds.ToList();
        var watched = WatchedUserIds.ToList();
        return query.Where(c =>
            c.SubjectUserId == uid
            || (c.FamilyDependentId != null && c.FamilyId != null && families.Contains(c.FamilyId.Value))
            || (c.SubjectUserId != null && watched.Contains(c.SubjectUserId.Value)));
    }

    public VaccinationAccessLevel AccessTo(Guid? subjectUserId, Guid? familyDependentId, Guid? familyId)
    {
        if (subjectUserId == UserId) return VaccinationAccessLevel.Full;
        if (familyDependentId is not null && familyId is { } f && FamilyIds.Contains(f)) return VaccinationAccessLevel.Full;
        if (subjectUserId is { } s && WatchedUserIds.Contains(s)) return VaccinationAccessLevel.Watch;
        return VaccinationAccessLevel.None;
    }

    public VaccinationAccessLevel AccessTo(Vaccination v) => AccessTo(v.SubjectUserId, v.FamilyDependentId, v.FamilyId);
    public VaccinationAccessLevel AccessTo(VaccinationCertificate c) => AccessTo(c.SubjectUserId, c.FamilyDependentId, c.FamilyId);
}

public class VaccinationAccess(AppDbContext db, SubjectScopeService scopeService)
{
    public async Task<VaccinationScope> GetScopeAsync(Guid userId, CancellationToken ct = default)
    {
        var s = await scopeService.GetScopeAsync(userId, ct);
        return new VaccinationScope(s.UserId, s.FamilyIds, s.AdminFamilyIds, s.WatchedUserIds);
    }

    /// <summary>Загрузить запись (с отслеживанием) и уровень доступа к ней; чужая — (null, None).</summary>
    public async Task<(Vaccination? Item, VaccinationAccessLevel Access, VaccinationScope Scope)> LoadAsync(
        Guid userId, Guid id, bool tracking, CancellationToken ct = default)
    {
        var scope = await GetScopeAsync(userId, ct);
        var query = tracking ? db.Vaccinations : db.Vaccinations.AsNoTracking();
        var item = await scope.Apply(query).FirstOrDefaultAsync(v => v.Id == id, ct);
        return item is null ? (null, VaccinationAccessLevel.None, scope) : (item, scope.AccessTo(item), scope);
    }

    public async Task<(VaccinationCertificate? Item, VaccinationAccessLevel Access)> LoadCertificateAsync(
        Guid userId, Guid id, CancellationToken ct = default)
    {
        var scope = await GetScopeAsync(userId, ct);
        var item = await scope.ApplyCertificates(db.VaccinationCertificates.AsNoTracking()).FirstOrDefaultAsync(c => c.Id == id, ct);
        return item is null ? (null, VaccinationAccessLevel.None) : (item, scope.AccessTo(item));
    }
}
