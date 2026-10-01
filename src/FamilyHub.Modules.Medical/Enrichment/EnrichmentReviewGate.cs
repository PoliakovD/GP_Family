using System.Text.Json;
using System.Text.Json.Serialization;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Modules.Medical.Extraction;

namespace FamilyHub.Modules.Medical.Enrichment;

/// <summary>Сниппет, на котором построен черновик результата. Хранится в самом черновике
/// (DraftPayloadJson), а не берётся из кэша поиска в момент ревью: кэш могли обновить
/// (SearchCacheWarmupJob/рефреш), и индексы UsedSourceIndexes перестали бы указывать на те же
/// сниппеты, а повторная суммаризация («пересуммаризировать») обязана идти ровно по тем же.</summary>
public record DraftSnippet(string Title, string Url, string Text);

/// <summary>Черновик результата для препарата (MedicationEnrichmentProcessor/Visit…). Хранит уже
/// разрешённые процессором NormalizedName/DisplayName/ExtraAliases (коррекция названия по
/// схожести, см. MedicationNameCorrection) и Source — одобрение пишет в kb ровно то, что писал бы
/// процессор при достаточной уверенности.</summary>
public record MedicationDraft(
    string NormalizedName, string DisplayName, IReadOnlyList<string>? ExtraAliases, string Source,
    MedicationSummary Summary, IReadOnlyList<DraftSnippet> Snippets);

/// <summary>Черновик результата для показателя (LabAnalyteEnrichmentProcessor) — Summary уже после
/// детерминированного ReferenceRangeMerger.</summary>
public record LabAnalyteDraft(
    string NormalizedName, Guid SpecimenKbId, string DisplayName, string Source,
    LabAnalyteSummary Summary, IReadOnlyList<DraftSnippet> Snippets);

public static class EnrichmentDraftSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T draft) => JsonSerializer.Serialize(draft, Options);

    public static T? Deserialize<T>(string json) where T : class => JsonSerializer.Deserialize<T>(json, Options);

    public static IReadOnlyList<DraftSnippet> ToDraftSnippets(IEnumerable<FamilyHub.Infrastructure.Enrichment.WebSnippet> snippets) =>
        snippets.Select(s => new DraftSnippet(s.Title, s.Url, s.Text)).ToList();

    public static IReadOnlyList<FamilyHub.Infrastructure.Enrichment.WebSnippet> ToWebSnippets(IEnumerable<DraftSnippet> snippets) =>
        snippets.Select(s => new FamilyHub.Infrastructure.Enrichment.WebSnippet(s.Title, s.Url, s.Text)).ToList();
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
        // Предложенный запрос — нормализованное имя (именно оно уходит в провайдер и в шаблоны
        // поисковых запросов, см. AnalyteSearchQueryBuilder/MedicationSearchQueryBuilder); админ
        // может поправить его до одобрения. Уже заданное (повторная парковка после ретрая) не трём.
        job.ProposedQueryText ??= job.NormalizedName;
        job.Status = EnrichmentJobStatus.AwaitingSearchApproval;
        job.Error = null;
        return true;
    }

    /// <summary>Текст запроса, который уйдёт в провайдер: правка админа, иначе нормализованное имя.</summary>
    public static string EffectiveQuery(IReviewableEnrichmentJob job) =>
        string.IsNullOrWhiteSpace(job.ProposedQueryText) ? job.NormalizedName : job.ProposedQueryText.Trim();

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
