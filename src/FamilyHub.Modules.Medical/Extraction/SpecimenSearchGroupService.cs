using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Kb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Extraction;

public record SpecimenSearchGroupItem(Guid Id, string DisplayName, string? SearchGroupKey, int CacheRows);

public enum SetSearchGroupResult { Ok, NotFound, Invalid }

public record SetSearchGroupOutcome(SetSearchGroupResult Result, string? Error = null, int MergedRows = 0, int CopiedRows = 0)
{
    public static SetSearchGroupOutcome NotFound { get; } = new(SetSearchGroupResult.NotFound);
}

/// <summary>
/// Группы поиска биоматериалов (ADR-0018): биоматериалы с одним <see cref="GlobalSpecimenKb.SearchGroupKey"/> делят
/// строку кэша платного поиска и поисковый запрос — АЧТВ × (кровь, венозная кровь, плазма) стоит один платный поиск,
/// а не три. Записи справочника (kb) остаются отдельными на каждый биоматериал. Присвоение группы схлопывает уже
/// накопленные строки кэша внутри группы (свежая побеждает, ручные сниппеты и override'ы проигравшей переносятся);
/// выход из группы даёт биоматериалу собственную копию строк группы — чтобы он не потерял кэш.
/// </summary>
public class SpecimenSearchGroupService(AppDbContext db, KbChangeLogService changeLog, ILogger<SpecimenSearchGroupService> logger)
{
    public const int MaxGroupKeyLength = 100;

    public async Task<List<SpecimenSearchGroupItem>> ListAsync(string? q, int take, CancellationToken ct = default)
    {
        var query = db.GlobalSpecimensKb.AsNoTracking()
            .Where(s => s.Id != SpecimenContextIds.Unresolved);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim().ToLowerInvariant();
            query = query.Where(s => s.NormalizedName.Contains(needle) || (s.SearchGroupKey != null && s.SearchGroupKey.Contains(needle)));
        }

        var specimens = await query
            .OrderBy(s => s.SearchGroupKey == null).ThenBy(s => s.SearchGroupKey).ThenBy(s => s.DisplayName)
            .Take(Math.Clamp(take, 1, 200))
            .Select(s => new { s.Id, s.DisplayName, s.SearchGroupKey })
            .ToListAsync(ct);

        var ids = specimens.Select(s => s.Id).ToList();
        var counts = await db.LabAnalyteSearchCaches.AsNoTracking()
            .Where(c => ids.Contains(c.SpecimenKbId))
            .GroupBy(c => c.SpecimenKbId).Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);

        return specimens.Select(s => new SpecimenSearchGroupItem(
            s.Id, s.DisplayName, SearchGroupKeys.Normalize(s.SearchGroupKey), counts.GetValueOrDefault(s.Id))).ToList();
    }

    public async Task<SetSearchGroupOutcome> SetGroupAsync(Guid specimenId, string? groupKey, CancellationToken ct = default)
    {
        if (specimenId == SpecimenContextIds.Unresolved)
            return new SetSearchGroupOutcome(SetSearchGroupResult.Invalid, "Системную запись «источник не определён» нельзя включать в группу.");

        var newGroup = SearchGroupKeys.Normalize(groupKey);
        if (newGroup is { Length: > MaxGroupKeyLength })
            return new SetSearchGroupOutcome(SetSearchGroupResult.Invalid, $"Название группы длиннее {MaxGroupKeyLength} символов.");

        var specimen = await db.GlobalSpecimensKb.FirstOrDefaultAsync(s => s.Id == specimenId, ct);
        if (specimen is null) return SetSearchGroupOutcome.NotFound;

        var oldKey = SearchGroupKeys.Effective(specimenId, specimen.SearchGroupKey);
        var newKey = SearchGroupKeys.Effective(specimenId, newGroup);
        specimen.SearchGroupKey = newGroup;
        if (oldKey == newKey)
        {
            await db.SaveChangesAsync(ct);
            return new SetSearchGroupOutcome(SetSearchGroupResult.Ok);
        }

        var merged = 0;
        var copied = 0;
        var oldRows = await db.LabAnalyteSearchCaches.Where(c => c.SearchGroupKey == oldKey).ToListAsync(ct);

        if (newGroup is not null)
        {
            // Вход в группу. Если прежний ключ — собственный ("specimen:<id>"), строки уезжают в группу
            // (с слиянием при коллизии); если биоматериал переходит из другой группы — старые строки остаются
            // другим её участникам, а здесь начинается кэш новой группы.
            if (oldKey.StartsWith(SearchGroupKeys.SpecimenPrefix, StringComparison.Ordinal))
            {
                foreach (var row in oldRows)
                {
                    var existing = await db.LabAnalyteSearchCaches
                        .FirstOrDefaultAsync(c => c.NormalizedName == row.NormalizedName && c.SearchGroupKey == newKey, ct);
                    if (existing is null)
                    {
                        var before = SearchCacheSnapshots.From(row);
                        row.SearchGroupKey = newKey;
                        await changeLog.RecordAsync(
                            KbChangeTarget.LabAnalyteSearchCache, row.Id, row.NormalizedName, "cache-merge",
                            KbChangeLogService.ToJson(before), KbChangeLogService.ToJson(SearchCacheSnapshots.From(row)),
                            KbChangeLogService.ActorAdmin, $"Строка передана группе поиска «{newGroup}»", ct: ct);
                    }
                    else
                    {
                        await MergeRowsAsync(existing, row, ct);
                        merged++;
                    }
                }
            }
        }
        else
        {
            // Выход из группы — биоматериал получает собственные копии строк группы.
            foreach (var row in oldRows)
            {
                var copy = new LabAnalyteSearchCache
                {
                    Id = Guid.NewGuid(), NormalizedName = row.NormalizedName, SpecimenKbId = specimenId, SearchGroupKey = newKey,
                    Provider = row.Provider, LastUpdatedAt = row.LastUpdatedAt, CanBeUpdatedAfter = row.CanBeUpdatedAfter,
                    SnippetsJson = row.SnippetsJson, OverridesJson = row.OverridesJson,
                };
                db.LabAnalyteSearchCaches.Add(copy);
                copied++;
                await changeLog.RecordAsync(
                    KbChangeTarget.LabAnalyteSearchCache, copy.Id, copy.NormalizedName, "cache-merge",
                    null, KbChangeLogService.ToJson(SearchCacheSnapshots.From(copy)), KbChangeLogService.ActorAdmin,
                    $"Копия кэша группы «{SearchGroupKeys.Normalize(oldKey[SearchGroupKeys.GroupPrefix.Length..])}» при выходе биоматериала из группы",
                    ct: ct);
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Группа поиска биоматериала {SpecimenId}: «{Old}» → «{New}», слито строк кэша {Merged}, скопировано {Copied}.",
            specimenId, oldKey, newKey, merged, copied);
        return new SetSearchGroupOutcome(SetSearchGroupResult.Ok, null, merged, copied);
    }

    /// <summary>Сливает две строки кэша одного названия: побеждает более свежая; ручные сниппеты и override'ы
    /// проигравшей переносятся в победившую (то, что админ добавил руками, не теряется). Проигравшая удаляется.</summary>
    private async Task MergeRowsAsync(LabAnalyteSearchCache existing, LabAnalyteSearchCache incoming, CancellationToken ct)
    {
        var (winner, loser) = incoming.LastUpdatedAt > existing.LastUpdatedAt ? (incoming, existing) : (existing, incoming);
        var winnerBefore = SearchCacheSnapshots.From(winner);
        var loserBefore = SearchCacheSnapshots.From(loser);

        var snippets = SearchCacheSnippets.Parse(winner.SnippetsJson);
        var urls = snippets.Select(s => s.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        snippets.AddRange(SearchCacheSnippets.Parse(loser.SnippetsJson)
            .Where(s => (s.Origin == SnippetOrigin.Manual || s.Pinned) && !urls.Contains(s.Url)));
        winner.SnippetsJson = SearchCacheSnippets.Serialize(snippets);

        var overrides = SearchCacheSnippets.ParseOverrides(loser.OverridesJson);
        foreach (var (url, flag) in SearchCacheSnippets.ParseOverrides(winner.OverridesJson)) overrides[url] = flag;
        var finalUrls = snippets.Select(s => s.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        winner.OverridesJson = SearchCacheSnippets.SerializeOverrides(
            overrides.Where(kv => finalUrls.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));

        // Победитель занимает ключ группы (если победила собственная строка биоматериала, её ключ был "specimen:..."):
        // сначала удаляем проигравшую (освобождая уникальный ключ), затем перекладываем победителя — двумя
        // SaveChanges, потому что порядок DELETE/UPDATE внутри одного не гарантирован.
        var groupKey = existing.SearchGroupKey;
        db.LabAnalyteSearchCaches.Remove(loser);
        await db.SaveChangesAsync(ct);
        winner.SearchGroupKey = groupKey;
        await db.SaveChangesAsync(ct);
        await changeLog.RecordAsync(
            KbChangeTarget.LabAnalyteSearchCache, winner.Id, winner.NormalizedName, "cache-merge",
            KbChangeLogService.ToJson(winnerBefore), KbChangeLogService.ToJson(SearchCacheSnapshots.From(winner)),
            KbChangeLogService.ActorAdmin, "Слияние строк кэша внутри группы поиска", ct: ct);
        await changeLog.RecordAsync(
            KbChangeTarget.LabAnalyteSearchCache, loser.Id, loser.NormalizedName, "cache-merge",
            KbChangeLogService.ToJson(loserBefore), null, KbChangeLogService.ActorAdmin,
            $"Слита в строку {winner.Id}", ct: ct);
    }
}
