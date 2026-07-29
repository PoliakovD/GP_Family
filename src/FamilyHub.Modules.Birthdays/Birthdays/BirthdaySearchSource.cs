using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Birthdays.Birthdays;

/// <summary>
/// Реализация <see cref="IBirthdaySearchSource"/> — источник дней рождения для глобального поиска
/// (агрегируется в SearchService, Modules.Medical, через DI-абстракцию из Infrastructure).
/// In-memory поиск: PersonName зашифровано at-rest (ADR-0002), Postgres-FTS по нему физически
/// невозможен — тот же путь, что MedicalRecordService.SearchAsync для медкарт (ADR-0003): EF
/// расшифровывает поле конвертером при материализации, дальше матчинг через IRussianTextSearcher.
/// Скоуп доступа — семьи, где пользователь активный участник (как у Medication, а не как у
/// MedicalRecord: день рождения виден всем членам семьи, не только тому, кто его добавил —
/// см. BirthdayService, "семейный ресурс").
/// </summary>
public class BirthdaySearchSource(AppDbContext db, IFamilyAccessService access, IRussianTextSearcher searcher)
    : IBirthdaySearchSource
{
    public async Task<List<BirthdaySearchHit>> SearchAsync(
        Guid userId, string query, int limit, CancellationToken ct = default)
    {
        var familyIds = await access.GetActiveFamilyIdsAsync(userId, ct);
        if (familyIds.Count == 0) return [];

        var birthdays = await db.Birthdays.AsNoTracking()
            .Where(b => familyIds.Contains(b.FamilyId))
            .Include(b => b.Family)
            .ToListAsync(ct);

        var hits = new List<BirthdaySearchHit>();
        foreach (var b in birthdays)
        {
            var score = searcher.Score(b.PersonName, query);
            if (score > 0)
                hits.Add(new BirthdaySearchHit(b.Id, b.FamilyId, b.Family.Name, b.PersonName, b.Date, score));
        }

        return hits.OrderByDescending(h => h.Score).Take(limit).ToList();
    }
}
