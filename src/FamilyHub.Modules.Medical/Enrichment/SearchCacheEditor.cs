using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Kb;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.Enrichment;

public enum CacheEditResult { Ok, NotFound, Invalid }

public record CacheEditOutcome(CacheEditResult Result, string? Error = null, WebSnippet? Snippet = null)
{
    /// <summary>Сколько сниппетов добавлено импортом (<see cref="SearchCacheEditor.ImportFromAsync"/>).</summary>
    public int ImportedCount { get; init; }

    public static CacheEditOutcome Ok(WebSnippet? snippet = null) => new(CacheEditResult.Ok, null, snippet);
    public static CacheEditOutcome NotFound { get; } = new(CacheEditResult.NotFound);
    public static CacheEditOutcome Invalid(string error) => new(CacheEditResult.Invalid, error);
}

/// <summary>
/// Правка набора сниппетов строки кэша поиска из очереди «Одобрение» (ADR-0018): ручное добавление
/// (цитата со ссылкой или знание эксперта), закрепление, включение/выключение (override) и удаление.
/// Одна реализация на обе таблицы кэша (препараты/показатели) через <see cref="ISearchCacheRow"/>; каждая
/// правка пишется в журнал изменений (<see cref="KbChangeLogService"/>) со снимками «было/стало» —
/// с возможностью отката. Ручные сниппеты переживают автообновление кэша (SearchCacheSnippets).
/// </summary>
public class SearchCacheEditor(AppDbContext db, KbChangeLogService changeLog)
{
    public async Task<CacheEditOutcome> AddManualAsync(
        WebSearchTopic topic, Guid cacheId, string? kind, string? url, string? title, string? text, string? note,
        CancellationToken ct = default)
    {
        var row = await LoadAsync(topic, cacheId, ct);
        if (row is null) return CacheEditOutcome.NotFound;

        var (snippet, error) = SearchCacheSnippets.CreateManual(kind, url, title, text, note);
        if (snippet is null) return CacheEditOutcome.Invalid(error!);

        var snippets = SearchCacheSnippets.Parse(row.SnippetsJson);
        if (snippets.Any(s => string.Equals(s.Url, snippet.Url, StringComparison.OrdinalIgnoreCase)))
            return CacheEditOutcome.Invalid("Сниппет с такой ссылкой уже есть в наборе.");

        var before = SearchCacheSnapshots.From(row);
        snippets.Add(snippet);
        row.SnippetsJson = SearchCacheSnippets.Serialize(snippets);
        await SaveAndLogAsync(topic, row, before, "cache-add", $"Добавлен {snippet.Kind}: {snippet.Title}", ct);
        return CacheEditOutcome.Ok(snippet);
    }

    public async Task<CacheEditOutcome> RemoveAsync(WebSearchTopic topic, Guid cacheId, string url, CancellationToken ct = default)
    {
        var row = await LoadAsync(topic, cacheId, ct);
        if (row is null) return CacheEditOutcome.NotFound;

        var snippets = SearchCacheSnippets.Parse(row.SnippetsJson);
        var removed = snippets.RemoveAll(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return CacheEditOutcome.NotFound;

        var before = SearchCacheSnapshots.From(row);
        row.SnippetsJson = SearchCacheSnippets.Serialize(snippets);
        // override удалённого URL больше ни на что не влияет — вычищаем, чтобы не висел.
        var overrides = SearchCacheSnippets.ParseOverrides(row.OverridesJson);
        if (overrides.Remove(url)) row.OverridesJson = SearchCacheSnippets.SerializeOverrides(overrides);
        await SaveAndLogAsync(topic, row, before, "cache-remove", $"Удалён сниппет: {url}", ct);
        return CacheEditOutcome.Ok();
    }

    /// <summary>Закрепление включает сниппет (override=true): закреплённый всегда в наборе для суммаризатора
    /// и не отсекается MaxSnippets. Открепление снимает только закрепление — override остаётся как был.</summary>
    public async Task<CacheEditOutcome> SetPinnedAsync(WebSearchTopic topic, Guid cacheId, string url, bool pinned, CancellationToken ct = default)
    {
        var row = await LoadAsync(topic, cacheId, ct);
        if (row is null) return CacheEditOutcome.NotFound;

        var snippets = SearchCacheSnippets.Parse(row.SnippetsJson);
        var index = snippets.FindIndex(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return CacheEditOutcome.NotFound;

        var before = SearchCacheSnapshots.From(row);
        snippets[index] = snippets[index] with { Pinned = pinned };
        row.SnippetsJson = SearchCacheSnippets.Serialize(snippets);
        if (pinned)
        {
            var overrides = SearchCacheSnippets.ParseOverrides(row.OverridesJson);
            overrides[snippets[index].Url] = true;
            row.OverridesJson = SearchCacheSnippets.SerializeOverrides(overrides);
        }

        await SaveAndLogAsync(topic, row, before, "cache-pin", $"{(pinned ? "Закреплён" : "Откреплён")}: {url}", ct);
        return CacheEditOutcome.Ok();
    }

    /// <summary>enabled=null снимает override (дальше решает домен/ручное происхождение).</summary>
    public async Task<CacheEditOutcome> SetOverrideAsync(
        WebSearchTopic topic, Guid cacheId, string url, bool? enabled, CancellationToken ct = default)
    {
        var row = await LoadAsync(topic, cacheId, ct);
        if (row is null) return CacheEditOutcome.NotFound;

        var before = SearchCacheSnapshots.From(row);
        var overrides = SearchCacheSnippets.ParseOverrides(row.OverridesJson);
        if (enabled is null) overrides.Remove(url);
        else overrides[url] = enabled.Value;
        row.OverridesJson = SearchCacheSnippets.SerializeOverrides(overrides);

        await SaveAndLogAsync(topic, row, before, "cache-override",
            $"{(enabled is null ? "Сброшен override" : enabled.Value ? "Включён" : "Выключен")}: {url}", ct);
        return CacheEditOutcome.Ok();
    }

    /// <summary>Строка кэша считается «свежей» (CanBeUpdatedAfter в будущем) — следующий прогон задачи
    /// возьмёт сниппеты из кэша и не пойдёт в платный поиск. Нужна, когда админ решает использовать
    /// набор (свой, ручной или скопированный у «двойника») вместо платного поиска.</summary>
    public async Task<CacheEditOutcome> MarkFreshAsync(WebSearchTopic topic, Guid cacheId, int refreshIntervalMonths, CancellationToken ct = default)
    {
        var row = await LoadAsync(topic, cacheId, ct);
        if (row is null) return CacheEditOutcome.NotFound;

        var before = SearchCacheSnapshots.From(row);
        var now = DateTime.UtcNow;
        row.LastUpdatedAt = now;
        row.CanBeUpdatedAfter = now.AddMonths(Math.Max(refreshIntervalMonths, 1));
        await SaveAndLogAsync(topic, row, before, "cache-replace", "Набор принят вместо платного поиска", ct);
        return CacheEditOutcome.Ok();
    }

    /// <summary>Копирует набор сниппетов «двойника» в строку этой задачи (ADR-0018): авто-сниппеты берутся у источника,
    /// ручные сниппеты и override'ы получателя сохраняются, провайдер и свежесть — как у источника (копия бесплатна, платного
    /// запроса не было). Применяется только к анализам: у препаратов нет биоматериалов и, значит, двойников.</summary>
    public async Task<CacheEditOutcome> CopyFromAsync(Guid targetId, Guid sourceId, CancellationToken ct = default)
    {
        var target = await db.LabAnalyteSearchCaches.FirstOrDefaultAsync(c => c.Id == targetId, ct);
        var source = await db.LabAnalyteSearchCaches.AsNoTracking().FirstOrDefaultAsync(c => c.Id == sourceId, ct);
        if (target is null || source is null) return CacheEditOutcome.NotFound;
        if (target.NormalizedName != source.NormalizedName) return CacheEditOutcome.Invalid("Двойник — это кэш того же названия показателя.");

        var before = SearchCacheSnapshots.From(target);
        var own = SearchCacheSnippets.Parse(target.SnippetsJson);
        var merged = SearchCacheSnippets.MergeAfterSearch(own, SearchCacheSnippets.Parse(source.SnippetsJson)
            .Where(s => s.Origin == SnippetOrigin.Auto).ToList());
        target.SnippetsJson = SearchCacheSnippets.Serialize(merged);

        var overrides = SearchCacheSnippets.ParseOverrides(source.OverridesJson);
        foreach (var (url, flag) in SearchCacheSnippets.ParseOverrides(target.OverridesJson)) overrides[url] = flag;
        var urls = merged.Select(s => s.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        target.OverridesJson = SearchCacheSnippets.SerializeOverrides(
            overrides.Where(kv => urls.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
        target.Provider = source.Provider;
        target.LastUpdatedAt = source.LastUpdatedAt;
        target.CanBeUpdatedAfter = source.CanBeUpdatedAfter;

        await SaveAndLogAsync(WebSearchTopic.LabAnalyte, target, before, "cache-replace",
            $"Скопирован кэш двойника {sourceId}", ct);
        return CacheEditOutcome.Ok();
    }

    /// <summary>Правка заголовка/текста/заметки сниппета (<see cref="SearchCacheSnippets.ApplyEdit"/>): правленый
    /// авто-сниппет становится ручной цитатой и переживает автообновление кэша.</summary>
    public async Task<CacheEditOutcome> EditAsync(
        WebSearchTopic topic, Guid cacheId, string url, string? title, string? text, string? note, CancellationToken ct = default)
    {
        var row = await LoadAsync(topic, cacheId, ct);
        if (row is null) return CacheEditOutcome.NotFound;

        var snippets = SearchCacheSnippets.Parse(row.SnippetsJson);
        var index = snippets.FindIndex(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return CacheEditOutcome.NotFound;

        var (edited, error) = SearchCacheSnippets.ApplyEdit(snippets[index], title, text, note);
        if (edited is null) return CacheEditOutcome.Invalid(error!);

        var before = SearchCacheSnapshots.From(row);
        snippets[index] = edited;
        row.SnippetsJson = SearchCacheSnippets.Serialize(snippets);
        await SaveAndLogAsync(topic, row, before, "cache-edit", $"Правка сниппета: {edited.Title}", ct);
        return CacheEditOutcome.Ok(edited);
    }

    /// <summary>Импорт из любой другой строки кэша той же темы («взять из готового кэша» в очереди «Одобрение»):
    /// в отличие от <see cref="CopyFromAsync"/> имя источника может отличаться (торговое название/МНН, другое
    /// написание показателя). Выбранные сниппеты (urls=null — все) дописываются к своему набору без дублей,
    /// override'ы источника переносятся только для импортированных URL и не перетирают свои. Свежесть строки
    /// не трогается — её ставит <see cref="MarkFreshAsync"/>, когда админ принимает набор вместо платного поиска.</summary>
    public async Task<CacheEditOutcome> ImportFromAsync(
        WebSearchTopic topic, Guid targetId, Guid sourceId, IReadOnlyCollection<string>? urls, CancellationToken ct = default)
    {
        if (targetId == sourceId) return CacheEditOutcome.Invalid("Нельзя импортировать набор сам в себя.");

        var target = await LoadAsync(topic, targetId, ct);
        var source = topic == WebSearchTopic.Medication
            ? (ISearchCacheRow?)await db.MedicationSearchCaches.AsNoTracking().FirstOrDefaultAsync(c => c.Id == sourceId, ct)
            : await db.LabAnalyteSearchCaches.AsNoTracking().FirstOrDefaultAsync(c => c.Id == sourceId, ct);
        if (target is null || source is null) return CacheEditOutcome.NotFound;

        var (merged, imported) = SearchCacheSnippets.Import(
            SearchCacheSnippets.Parse(target.SnippetsJson), SearchCacheSnippets.Parse(source.SnippetsJson), urls);
        if (imported.Count == 0)
            return CacheEditOutcome.Invalid("Нечего добавить: выбранные источники уже есть в наборе (или ничего не выбрано).");

        var before = SearchCacheSnapshots.From(target);
        target.SnippetsJson = SearchCacheSnippets.Serialize(merged);

        var overrides = SearchCacheSnippets.ParseOverrides(target.OverridesJson);
        var importedUrls = imported.Select(s => s.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (url, flag) in SearchCacheSnippets.ParseOverrides(source.OverridesJson))
            if (importedUrls.Contains(url) && !overrides.ContainsKey(url)) overrides[url] = flag;
        target.OverridesJson = SearchCacheSnippets.SerializeOverrides(overrides);
        // Строка, созданная «ensure» до первого платного поиска, подписана "manual" — набор теперь из чужой выдачи.
        if (string.IsNullOrEmpty(target.Provider) || target.Provider == "manual") target.Provider = source.Provider;

        await SaveAndLogAsync(topic, target, before, "cache-import",
            $"Импортировано {imported.Count} из кэша «{source.NormalizedName}» ({sourceId})", ct);
        return new CacheEditOutcome(CacheEditResult.Ok, null, null) { ImportedCount = imported.Count };
    }

    private async Task<ISearchCacheRow?> LoadAsync(WebSearchTopic topic, Guid id, CancellationToken ct) =>
        topic == WebSearchTopic.Medication
            ? await db.MedicationSearchCaches.FirstOrDefaultAsync(c => c.Id == id, ct)
            : await db.LabAnalyteSearchCaches.FirstOrDefaultAsync(c => c.Id == id, ct);

    private async Task SaveAndLogAsync(
        WebSearchTopic topic, ISearchCacheRow row, SearchCacheSnapshot before, string action, string note, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        var target = topic == WebSearchTopic.Medication ? KbChangeTarget.MedicationSearchCache : KbChangeTarget.LabAnalyteSearchCache;
        await changeLog.RecordAsync(
            target, row.Id, row.NormalizedName, action,
            KbChangeLogService.ToJson(before), KbChangeLogService.ToJson(SearchCacheSnapshots.From(row)),
            KbChangeLogService.ActorAdmin, note, ct: ct);
    }
}
