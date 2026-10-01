using System.Text.Json;

namespace FamilyHub.Infrastructure.Enrichment;

/// <summary>
/// Чистые функции над списком сниппетов строки кэша поиска (SnippetsJson/OverridesJson) — общие для
/// MedicationSearchCacheService/LabAnalyteSearchCacheService и очереди «Одобрение» (ADR-0018). Главное
/// правило: ручные сниппеты (<see cref="SnippetOrigin.Manual"/>) и закреплённые переживают
/// автообновление кэша новым платным поиском — иначе правка админа исчезала бы при первом же рефреше.
/// </summary>
public static class SearchCacheSnippets
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public const int MaxNoteLength = 500;

    public static List<WebSnippet> Parse(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<WebSnippet>>(json, JsonOptions) ?? [];

    public static string Serialize(IEnumerable<WebSnippet> snippets) => JsonSerializer.Serialize(snippets, JsonOptions);

    public static Dictionary<string, bool> ParseOverrides(string? json) =>
        string.IsNullOrEmpty(json)
            ? new Dictionary<string, bool>()
            : JsonSerializer.Deserialize<Dictionary<string, bool>>(json, JsonOptions) ?? new Dictionary<string, bool>();

    /// <summary>null при пустом словаре — столбец остаётся NULL (прежнее поведение).</summary>
    public static string? SerializeOverrides(IReadOnlyDictionary<string, bool> overrides) =>
        overrides.Count == 0 ? null : JsonSerializer.Serialize(overrides, JsonOptions);

    public static bool HasManual(string? snippetsJson) => Parse(snippetsJson).Any(s => s.Origin == SnippetOrigin.Manual);

    /// <summary>Новый платный поиск поверх существующей строки: свежая выдача заменяет авто-сниппеты,
    /// но ручные остаются как есть, а закреплённые авто-сниппеты сохраняют закрепление (и остаются,
    /// даже если свежая выдача их не вернула).</summary>
    public static List<WebSnippet> MergeAfterSearch(IReadOnlyList<WebSnippet> existing, IReadOnlyList<WebSnippet> fresh)
    {
        var manual = existing.Where(s => s.Origin == SnippetOrigin.Manual).ToList();
        var manualUrls = manual.Select(s => s.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pinnedAuto = existing.Where(s => s.Origin == SnippetOrigin.Auto && s.Pinned).ToList();
        var pinnedUrls = pinnedAuto.Select(s => s.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<WebSnippet>();
        foreach (var s in fresh)
        {
            if (manualUrls.Contains(s.Url)) continue;
            result.Add(pinnedUrls.Contains(s.Url) ? s with { Pinned = true } : s);
        }

        var freshUrls = fresh.Select(s => s.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        result.AddRange(pinnedAuto.Where(s => !freshUrls.Contains(s.Url)));
        result.AddRange(manual);
        return result;
    }

    /// <summary>Полная замена списка из админки (PUT /search-cache/{id}): присланный список — как есть,
    /// но ручные сниппеты, которых в нём нет, дописываются обратно — старый редактор кэша не знает
    /// про ручные записи и не должен молча стирать их. Удалить ручной сниппет можно только явно.</summary>
    public static List<WebSnippet> MergeAfterReplace(IReadOnlyList<WebSnippet> existing, IReadOnlyList<WebSnippet> incoming)
    {
        var incomingUrls = incoming.Select(s => s.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = incoming.ToList();
        result.AddRange(existing.Where(s => s.Origin == SnippetOrigin.Manual && !incomingUrls.Contains(s.Url)));
        return result;
    }

    /// <summary>Правка сниппета админом (очередь «Одобрение»): заголовок, текст, заметка. Текст не пустой и не длиннее
    /// большего из <see cref="SnippetKinds.MaxManualTextLength"/> и исходной длины — авто-выдержку можно подрезать или
    /// поправить, но не превратить в статью. Правленый авто-сниппет становится ручной цитатой (Manual + manual-quote):
    /// иначе следующий платный поиск (<see cref="MergeAfterSearch"/>) молча заменил бы правку свежей выдачей.
    /// Знание эксперта остаётся знанием эксперта. URL, закрепление — без изменений.</summary>
    public static (WebSnippet? Snippet, string? Error) ApplyEdit(WebSnippet original, string? title, string? text, string? note)
    {
        var body = text?.Trim();
        if (string.IsNullOrEmpty(body)) return (null, "Текст не может быть пустым.");
        var limit = Math.Max(SnippetKinds.MaxManualTextLength, original.Text.Length);
        if (body.Length > limit)
            return (null, $"Текст длиннее {limit} символов — оставьте короткую выдержку, не статью целиком.");

        var cleanNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (cleanNote is { Length: > MaxNoteLength }) return (null, $"Заметка длиннее {MaxNoteLength} символов.");

        var expert = original.Kind == SnippetKinds.ExpertKnowledge;
        var cleanTitle = !string.IsNullOrWhiteSpace(title)
            ? title.Trim()
            : expert ? SnippetKinds.ExpertSourceLabel
            : Uri.TryCreate(original.Url, UriKind.Absolute, out var uri) ? uri.Host : original.Title;

        return (original with
        {
            Title = cleanTitle,
            Text = body,
            Note = cleanNote,
            Origin = SnippetOrigin.Manual,
            Kind = expert ? SnippetKinds.ExpertKnowledge : SnippetKinds.ManualQuote,
        }, null);
    }

    /// <summary>Импорт сниппетов из чужой строки кэша (очередь «Одобрение»: «взять из готового кэша»): выбранные URL
    /// (null — все) дописываются к своему набору, дубли по URL пропускаются — свои сниппеты (в том числе правленые)
    /// не перетираются. Происхождение/вид/закрепление сохраняются как у источника.</summary>
    public static (List<WebSnippet> Merged, List<WebSnippet> Imported) Import(
        IReadOnlyList<WebSnippet> own, IReadOnlyList<WebSnippet> source, IReadOnlyCollection<string>? urls)
    {
        var wanted = urls is null ? null : new HashSet<string>(urls, StringComparer.OrdinalIgnoreCase);
        var present = own.Select(s => s.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var imported = new List<WebSnippet>();
        foreach (var s in source)
        {
            if (wanted is not null && !wanted.Contains(s.Url)) continue;
            if (!present.Add(s.Url)) continue;
            imported.Add(s);
        }

        return ([.. own, .. imported], imported);
    }

    /// <summary>Строит ручной сниппет по вводу админа или возвращает текст ошибки. manual-quote —
    /// ссылка на источник + короткая цитата; expert-knowledge — знание без URL (служебный
    /// expert://admin/&lt;id&gt;, подпись «Эксперт: админ»). Целые статьи не принимаются: лимит
    /// <see cref="SnippetKinds.MaxManualTextLength"/>. Недоверенный домен — не ошибка (предупреждение в UI).</summary>
    public static (WebSnippet? Snippet, string? Error) CreateManual(
        string? kind, string? url, string? title, string? text, string? note)
    {
        if (kind is not (SnippetKinds.ManualQuote or SnippetKinds.ExpertKnowledge))
            return (null, "Вид сниппета: manual-quote или expert-knowledge.");

        var body = text?.Trim();
        if (string.IsNullOrEmpty(body)) return (null, "Текст не может быть пустым.");
        if (body.Length > SnippetKinds.MaxManualTextLength)
            return (null, $"Текст длиннее {SnippetKinds.MaxManualTextLength} символов — вставьте короткую выдержку, не статью целиком.");

        var cleanNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (cleanNote is { Length: > MaxNoteLength }) return (null, $"Заметка длиннее {MaxNoteLength} символов.");

        if (kind == SnippetKinds.ExpertKnowledge)
        {
            var expertTitle = string.IsNullOrWhiteSpace(title) ? SnippetKinds.ExpertSourceLabel : title.Trim();
            return (new WebSnippet(
                expertTitle, SnippetKinds.ExpertUrlPrefix + Guid.NewGuid().ToString("N"), body,
                SnippetOrigin.Manual, SnippetKinds.ExpertKnowledge, cleanNote), null);
        }

        var cleanUrl = url?.Trim();
        if (string.IsNullOrEmpty(cleanUrl) ||
            !Uri.TryCreate(cleanUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return (null, "Для цитаты нужна ссылка на источник (http/https).");

        var quoteTitle = string.IsNullOrWhiteSpace(title) ? uri.Host : title.Trim();
        return (new WebSnippet(quoteTitle, cleanUrl, body, SnippetOrigin.Manual, SnippetKinds.ManualQuote, cleanNote), null);
    }
}
