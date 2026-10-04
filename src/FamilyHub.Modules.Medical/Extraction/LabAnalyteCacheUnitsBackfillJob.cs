using System.Text;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Prompts;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static FamilyHub.Infrastructure.LmStudio.LmStudioPayloadReader;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Разовая ручная джоба (кнопка в админке «Операции»): строки кэша платного поиска, накопленные ДО
/// появления единиц измерения в запросе (LabAnalyteEnrichmentJob.Units), не знают, для каких единиц
/// в сохранённой выдаче есть нормы. Один LLM-вызов на строку кэша читает сниппеты и перечисляет
/// единицы, в которых выдача действительно даёт референсные значения → LabAnalyteSearchCache.Units.
/// Дальше LabAnalyteEnrichmentProcessor при «пробеле по единице» переиспользует такой кэш (даже
/// устаревший) без нового платного запроса, если нужная единица в нём уже есть.
///
/// Антигаллюцинационный гейт: единица принимается, только если она буквально встречается в тексте
/// сниппетов (без учёта регистра/пробелов). Строки обрабатываются батчами (BatchSize), каждый батч
/// ставит следующий — единственный воркер очереди enrichment не блокируется на сотни вызовов подряд.
/// Units == null — строка ещё не проверялась; "" — проверена, единиц в выдаче не найдено.
/// </summary>
[Queue("enrichment")]
[AutomaticRetry(Attempts = 3, DelaysInSeconds = [60, 600, 3600])]
public class LabAnalyteCacheUnitsBackfillJob(
    AppDbContext db, ILmStudioJsonClient client, IPromptProvider promptProvider,
    IBackgroundJobClient backgroundJobs, ILogger<LabAnalyteCacheUnitsBackfillJob> logger)
{
    public const int BatchSize = 20;
    private const int MaxSnippetChars = 700;
    private const int MaxTotalChars = 6000;

    private const string Prompt = """
        Ты — помощник по лабораторным справочникам. На входе — название показателя и фрагменты веб-страниц
        (результаты поиска). Перечисли единицы измерения, в которых во фрагментах ДЕЙСТВИТЕЛЬНО приведены
        референсные значения или результаты этого показателя (например "г/л", "ммоль/л", "%", "мкмоль/л").
        Верни ТОЛЬКО валидный JSON без пояснений и markdown: {"units": ["г/л", "ммоль/л"]}.
        Правила: единицу пиши так, как она напечатана во фрагментах; не добавляй единицы из общих знаний,
        если их нет в тексте; если единиц нет — {"units": []}. Верни строго один JSON-объект.
        """;

    public async Task RunAsync(CancellationToken ct = default)
    {
        var batch = await db.LabAnalyteSearchCaches
            .Where(c => c.Units == null && c.SnippetsJson != null && c.SnippetsJson != "[]")
            .OrderBy(c => c.LastUpdatedAt).Take(BatchSize).ToListAsync(ct);
        if (batch.Count == 0)
        {
            logger.LogInformation("LabAnalyteCacheUnitsBackfillJob: непроверенных строк кэша нет.");
            return;
        }

        LmStudioUnavailableException? unavailable = null;
        var done = 0;
        foreach (var cache in batch)
        {
            var snippets = FamilyHub.Infrastructure.Enrichment.SearchCacheSnippets.Parse(cache.SnippetsJson);
            if (snippets.Count == 0) { cache.Units = ""; continue; }

            var text = BuildSnippetText(snippets);
            var result = await client.ExtractJsonAsync(
                await promptProvider.GetAsync("analysis.cache-units", Prompt, ct),
                $"Показатель: {cache.DisplayName ?? cache.NormalizedName}\n\n{text}", ct, suppressThinking: true, shortTimeout: true);
            if (!result.Success || result.Payload is null)
            {
                if (result.IsTransient) { unavailable = new LmStudioUnavailableException(result.Error ?? "ИИ недоступен."); break; }
                continue; // смысловой отказ — строку оставим непроверенной, повторный запуск попробует снова
            }

            var haystack = Normalize(text);
            var units = ReadStringArray(result.Payload, "units")
                .Select(u => u.Trim()).Where(u => u.Length is > 0 and <= 20 && haystack.Contains(Normalize(u)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            cache.Units = string.Join("; ", units);
            done++;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("LabAnalyteCacheUnitsBackfillJob: проверено {Done} из {Batch} строк кэша.", done, batch.Count);

        if (unavailable is not null) throw unavailable;
        if (done > 0 || batch.Any(c => c.Units is not null))
            backgroundJobs.Enqueue<LabAnalyteCacheUnitsBackfillJob>(j => j.RunAsync(CancellationToken.None));
    }

    private static string BuildSnippetText(IReadOnlyList<FamilyHub.Infrastructure.Enrichment.WebSnippet> snippets)
    {
        var sb = new StringBuilder();
        foreach (var s in snippets)
        {
            var t = s.Text.Length > MaxSnippetChars ? s.Text[..MaxSnippetChars] : s.Text;
            sb.Append("- ").AppendLine(t);
            if (sb.Length >= MaxTotalChars) break;
        }
        return sb.ToString();
    }

    private static string Normalize(string v) => new(v.Where(c => !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant).ToArray());
}
