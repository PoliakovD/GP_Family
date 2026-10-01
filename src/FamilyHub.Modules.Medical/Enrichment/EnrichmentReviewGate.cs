using System.Text.Json;
using System.Text.Json.Serialization;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Extraction;

namespace FamilyHub.Modules.Medical.Enrichment;

/// <summary>Сниппет, на котором построен черновик результата. Хранится в самом черновике
/// (DraftPayloadJson), а не берётся из кэша поиска в момент ревью: кэш могли обновить
/// (SearchCacheWarmupJob/рефреш), и индексы UsedSourceIndexes перестали бы указывать на те же
/// сниппеты, а повторная суммаризация («пересуммаризировать») обязана идти ровно по тем же.</summary>
public record DraftSnippet(
    string Title, string Url, string Text,
    FamilyHub.Infrastructure.Enrichment.SnippetOrigin Origin = FamilyHub.Infrastructure.Enrichment.SnippetOrigin.Auto,
    string? Kind = null);

/// <summary>Черновик результата для препарата (MedicationEnrichmentProcessor/Visit…). Хранит уже
/// разрешённые процессором NormalizedName/DisplayName/ExtraAliases (коррекция названия по
/// схожести, см. MedicationNameCorrection) и Source — одобрение пишет в kb ровно то, что писал бы
/// процессор при достаточной уверенности.</summary>
public record MedicationDraft(
    string NormalizedName, string DisplayName, IReadOnlyList<string>? ExtraAliases, string Source,
    MedicationSummary Summary, IReadOnlyList<DraftSnippet> Snippets,
    IReadOnlyDictionary<string, List<int>>? FieldSources = null);

/// <summary>Черновик результата для показателя (LabAnalyteEnrichmentProcessor) — Summary уже после
/// детерминированного ReferenceRangeMerger.</summary>
public record LabAnalyteDraft(
    string NormalizedName, Guid SpecimenKbId, string DisplayName, string Source,
    LabAnalyteSummary Summary, IReadOnlyList<DraftSnippet> Snippets,
    IReadOnlyDictionary<string, List<int>>? FieldSources = null);

public static class EnrichmentDraftSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T draft) => JsonSerializer.Serialize(draft, Options);

    public static T? Deserialize<T>(string json) where T : class => JsonSerializer.Deserialize<T>(json, Options);

    public static IReadOnlyList<DraftSnippet> ToDraftSnippets(IEnumerable<FamilyHub.Infrastructure.Enrichment.WebSnippet> snippets) =>
        snippets.Select(s => new DraftSnippet(s.Title, s.Url, s.Text, s.Origin, s.Kind)).ToList();

    public static IReadOnlyList<FamilyHub.Infrastructure.Enrichment.WebSnippet> ToWebSnippets(IEnumerable<DraftSnippet> snippets) =>
        snippets.Select(s => new FamilyHub.Infrastructure.Enrichment.WebSnippet(s.Title, s.Url, s.Text, s.Origin, s.Kind)).ToList();
}

/// <summary>
/// Общая для трёх процессоров обогащения логика двух гейтов ручного одобрения (ADR-0018) —
/// чтобы условие «когда останавливаемся» и форма записи в задачу не расходились между
/// MedicationEnrichmentProcessor/VisitMedicationEnrichmentProcessor/LabAnalyteEnrichmentProcessor.
/// </summary>
public static class EnrichmentReviewGate
{
    /// <summary>Гейт 1 (платный поиск). Вызывается на кэш-промахе при реальном (не Null)
    /// провайдере: true — задача припаркована в AwaitingSearchApproval, процессор обязан сделать
    /// SaveChanges и выйти БЕЗ исключения (как для Deferred). false — поиск уже одобрен
    /// (SearchApprovedAt), продолжаем; вентиль (IsPausedAsync) проверяется ПОСЛЕ и поверх.</summary>
    public static bool TryParkForSearchApproval(
        IReviewableEnrichmentJob job, double? queryConfidence, string? queryConfidenceReason)
    {
        if (job.SearchApprovedAt is not null) return false;

        job.QueryConfidence = queryConfidence;
        job.QueryConfidenceReason = queryConfidenceReason;
        // Предложенный запрос (именно он уходит в провайдер и в шаблоны поисковых запросов, см.
        // AnalyteSearchQueryBuilder/MedicationSearchQueryBuilder); админ может поправить его до
        // одобрения. Уже заданное (повторная парковка после ретрая) не трём.
        job.ProposedQueryText ??= DefaultQuery(job);
        job.Status = EnrichmentJobStatus.AwaitingSearchApproval;
        job.Error = null;
        return true;
    }

    /// <summary>"brave: vidal.ru, rlsnet.ru" — провайдер + реально использованные модельным ответом домены.
    /// Общий для процессоров и очереди «Одобрение» (пересуммаризация строит ту же подпись источника).</summary>
    public static string BuildSourceLabel(
        string providerName, IReadOnlyList<FamilyHub.Infrastructure.Enrichment.WebSnippet> snippets, IReadOnlyList<int> usedIndexes)
    {
        var domains = usedIndexes
            .Where(i => i >= 0 && i < snippets.Count)
            .Select(i => FamilyHub.Infrastructure.Enrichment.SnippetKinds.IsExpertUrl(snippets[i].Url)
                ? FamilyHub.Infrastructure.Enrichment.SnippetKinds.ExpertSourceLabel
                : Uri.TryCreate(snippets[i].Url, UriKind.Absolute, out var uri) ? uri.Host : null)
            .Where(host => host is not null)
            .Distinct()
            .ToList();

        return domains.Count == 0 ? providerName : $"{providerName}: {string.Join(", ", domains)}";
    }

    /// <summary>Текст запроса, который уйдёт в провайдер: правка админа, иначе <see cref="DefaultQuery"/>.</summary>
    public static string EffectiveQuery(IReviewableEnrichmentJob job) =>
        string.IsNullOrWhiteSpace(job.ProposedQueryText) ? DefaultQuery(job) : job.ProposedQueryText.Trim();

    /// <summary>Запрос по умолчанию. Для показателя — читаемое название из бланка («MCH (среднее содержание Hb в
    /// эритроците)»), а НЕ нормализованное имя: ключ показателя (<see cref="LabAnalyteNormalizer.NormalizeAnalyteKey"/>)
    /// сворачивает латиницу в кириллицу фонетически («MCV» → «мкв», «RDW» → «рдв»), и поиск по такому ключу
    /// бессмыслен (платный запрос впустую). Для препаратов нормализованное имя остаётся как было.</summary>
    public static string DefaultQuery(IReviewableEnrichmentJob job)
    {
        if (job is LabAnalyteEnrichmentJob)
        {
            var readable = LabAnalyteNameCleaner.Clean(job.SourceDisplayName);
            if (readable.Length > 0) return readable;
        }

        return job.NormalizedName;
    }

    /// <summary>Гейт 2 (результат): true — уверенность суммаризатора ниже порога ИЛИ отсутствует
    /// (null считается «ниже», безопасный дефолт) — результат нужно отправить на ревью, а не в kb.</summary>
    public static bool NeedsResultReview(double? resultConfidence, double threshold) =>
        !EnrichmentReviewThresholds.IsConfident(resultConfidence, threshold);

    /// <summary>Записывает черновик в задачу и переводит её в AwaitingResultReview (kb не трогается).</summary>
    public static void ParkForResultReview(
        IReviewableEnrichmentJob job, string draftJson, double? resultConfidence, string? resultConfidenceReason)
    {
        job.DraftPayloadJson = draftJson;
        job.ResultConfidence = resultConfidence;
        job.ResultConfidenceReason = resultConfidenceReason;
        job.Status = EnrichmentJobStatus.AwaitingResultReview;
        job.Error = null;
    }

    /// <summary>Объединённая оценка этапа запроса из нескольких стражей (легитимность + правдоподобность
    /// для ручного ввода показателя): берётся минимум, любая отсутствующая → null (безопасно: «ниже порога»).
    /// Причина — через « · » из непустых.</summary>
    public static (double? Confidence, string? Reason) CombineQueryConfidence(
        (double? Confidence, string? Reason) first, (double? Confidence, string? Reason)? second)
    {
        if (second is null) return first;
        double? confidence = first.Confidence is { } a && second.Value.Confidence is { } b ? Math.Min(a, b) : null;
        var reasons = new[] { first.Reason, second.Value.Reason }.Where(r => !string.IsNullOrWhiteSpace(r));
        return (confidence, string.Join(" · ", reasons) is { Length: > 0 } joined ? joined : null);
    }
}
