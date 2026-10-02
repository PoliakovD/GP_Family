using System.Text.Json;
using System.Text.Json.Nodes;
using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.Messaging;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Enrichment;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.Kb;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Features.Admin;

/// <summary>
/// Очередь «Одобрение» (ADR-0018): ручное одобрение платных поисков и результатов обогащения
/// справочников препаратов/показателей. Единственный код, который выводит задачу из
/// <see cref="EnrichmentJobStatus.AwaitingSearchApproval"/>/<see cref="EnrichmentJobStatus.AwaitingResultReview"/>
/// — ни DeferredEnrichmentReleaseJob, ни LmStudioRecoverySweepJob, ни retry из «Задач» их не трогают.
///
/// Работает с тремя видами задач (препарат из аптечки, препарат из заключения врача, показатель)
/// через общий <see cref="IReviewableEnrichmentJob"/>; вид выбирается строкой <see cref="ReviewKinds"/>.
/// Админ платформы — отдельная учётка без строки User, поэтому ReviewedByUserId остаётся null
/// (поле зарезервировано) — аудит решений ведёт сам факт смены статуса/ReviewedAt, заметка админа и журнал
/// изменений kb (<see cref="KbChangeLogService"/>).
/// </summary>
public class AdminEnrichmentReviewService(
    AppDbContext db,
    IEnrichmentReviewConfigService config,
    KbWriter kbWriter,
    LabAnalyteKbWriter labWriter,
    AdminCatalogService catalog,
    MedicationSummarizer medicationSummarizer,
    LabAnalyteKbSummarizer labSummarizer,
    EnrichmentTrustedDomainService trustedDomains,
    MedicationSearchCacheService medCache,
    LabAnalyteSearchCacheService labCache,
    SearchCacheEditor cacheEditor,
    IOptions<EnrichmentOptions> options,
    IDomainEventPublisher publisher,
    IBackgroundJobClient backgroundJobs,
    ILogger<AdminEnrichmentReviewService> logger)
{
    /// <summary>Потолок строк на вид и стадию в выдаче очереди — очередь ручная, тысячи строк не ожидаются;
    /// сортировка «ниже порога первыми» делается в памяти поверх этой выборки.</summary>
    private const int MaxRowsPerKind = 500;

    private const int MaxQueryLength = 300;
    private const int MaxNoteLength = 2000;

    public const string ModeSearch = "search";
    public const string ModeUseCache = "use-cache";

    // ------------------------------------------------------------------ счётчики

    public async Task<ReviewQueueCountsDto> GetCountsAsync(CancellationToken ct = default)
    {
        var searches = new Dictionary<string, int>
        {
            [ReviewKinds.LabAnalyte] = await db.LabAnalyteEnrichmentJobs.CountAsync(j => j.Status == EnrichmentJobStatus.AwaitingSearchApproval, ct),
            [ReviewKinds.Medication] = await db.MedicationEnrichmentJobs.CountAsync(j => j.Status == EnrichmentJobStatus.AwaitingSearchApproval, ct),
            [ReviewKinds.VisitMedication] = await db.VisitMedicationEnrichmentJobs.CountAsync(j => j.Status == EnrichmentJobStatus.AwaitingSearchApproval, ct),
        };
        var results = new Dictionary<string, int>
        {
            [ReviewKinds.LabAnalyte] = await db.LabAnalyteEnrichmentJobs.CountAsync(j => j.Status == EnrichmentJobStatus.AwaitingResultReview, ct),
            [ReviewKinds.Medication] = await db.MedicationEnrichmentJobs.CountAsync(j => j.Status == EnrichmentJobStatus.AwaitingResultReview, ct),
            [ReviewKinds.VisitMedication] = await db.VisitMedicationEnrichmentJobs.CountAsync(j => j.Status == EnrichmentJobStatus.AwaitingResultReview, ct),
        };
        return new ReviewQueueCountsDto(searches.Values.Sum(), results.Values.Sum(), searches, results);
    }

    // ------------------------------------------------------------------ списки

    private record QueueRow(
        Guid Id, string Kind, string Name, string NormalizedName, Guid? SpecimenKbId, string? Specimen, string Origin,
        string? ProposedQuery, double? QueryConfidence, string? QueryConfidenceReason,
        double? ResultConfidence, string? ResultConfidenceReason, string? Provider, DateTime CreatedAt, bool HasNote);

    private async Task<List<QueueRow>> LoadRowsAsync(string? kind, EnrichmentJobStatus status, CancellationToken ct)
    {
        var rows = new List<QueueRow>();

        if (kind is null or ReviewKinds.LabAnalyte)
        {
            var lab = await db.LabAnalyteEnrichmentJobs.AsNoTracking()
                .Where(j => j.Status == status)
                .OrderBy(j => j.CreatedAt).Take(MaxRowsPerKind)
                .Select(j => new
                {
                    j.Id, j.SourceDisplayName, j.NormalizedName, j.SpecimenKbId, j.Origin, j.ProposedQueryText, j.QueryConfidence,
                    j.QueryConfidenceReason, j.ResultConfidence, j.ResultConfidenceReason, j.Provider, j.CreatedAt,
                    HasNote = j.ReviewNote != null,
                    Specimen = db.GlobalSpecimensKb.Where(s => s.Id == j.SpecimenKbId).Select(s => s.DisplayName).FirstOrDefault(),
                })
                .ToListAsync(ct);
            rows.AddRange(lab.Select(j => new QueueRow(
                j.Id, ReviewKinds.LabAnalyte, j.SourceDisplayName, j.NormalizedName, j.SpecimenKbId, j.Specimen, LabOriginCode(j.Origin),
                j.ProposedQueryText, j.QueryConfidence, j.QueryConfidenceReason, j.ResultConfidence, j.ResultConfidenceReason,
                j.Provider, j.CreatedAt, j.HasNote)));
        }

        if (kind is null or ReviewKinds.Medication)
        {
            var med = await db.MedicationEnrichmentJobs.AsNoTracking()
                .Where(j => j.Status == status)
                .OrderBy(j => j.CreatedAt).Take(MaxRowsPerKind)
                .Select(j => new
                {
                    j.Id, j.SourceDisplayName, j.NormalizedName, j.ProposedQueryText, j.QueryConfidence,
                    j.QueryConfidenceReason, j.ResultConfidence, j.ResultConfidenceReason, j.Provider, j.CreatedAt,
                    HasNote = j.ReviewNote != null,
                })
                .ToListAsync(ct);
            rows.AddRange(med.Select(j => new QueueRow(
                j.Id, ReviewKinds.Medication, j.SourceDisplayName, j.NormalizedName, null, null, "medkit",
                j.ProposedQueryText, j.QueryConfidence, j.QueryConfidenceReason, j.ResultConfidence, j.ResultConfidenceReason,
                j.Provider, j.CreatedAt, j.HasNote)));
        }

        if (kind is null or ReviewKinds.VisitMedication)
        {
            var visit = await db.VisitMedicationEnrichmentJobs.AsNoTracking()
                .Where(j => j.Status == status)
                .OrderBy(j => j.CreatedAt).Take(MaxRowsPerKind)
                .Select(j => new
                {
                    j.Id, j.SourceDisplayName, j.NormalizedName, j.ProposedQueryText, j.QueryConfidence,
                    j.QueryConfidenceReason, j.ResultConfidence, j.ResultConfidenceReason, j.Provider, j.CreatedAt,
                    HasNote = j.ReviewNote != null,
                })
                .ToListAsync(ct);
            rows.AddRange(visit.Select(j => new QueueRow(
                j.Id, ReviewKinds.VisitMedication, j.SourceDisplayName, j.NormalizedName, null, null, "visit",
                j.ProposedQueryText, j.QueryConfidence, j.QueryConfidenceReason, j.ResultConfidence, j.ResultConfidenceReason,
                j.Provider, j.CreatedAt, j.HasNote)));
        }

        return rows;
    }

    private static string LabOriginCode(EnrichmentRequestOrigin origin) => origin switch
    {
        EnrichmentRequestOrigin.ManualEntry => "manual",
        EnrichmentRequestOrigin.SystemMaintenance => "maintenance",
        _ => "extraction",
    };

    /// <summary>Единый Inbox: поиски и результаты вместе. Сортировка: «ниже порога» (в том числе без оценки)
    /// первыми, внутри — старые раньше (FIFO), чтобы ничто не залёживалось.</summary>
    public async Task<ReviewInboxResponse> ListInboxAsync(string? kind, string? stage, int skip, int take, CancellationToken ct = default)
    {
        var thresholds = await config.GetAsync(ct);
        var items = new List<ReviewInboxItemDto>();
        var searchCount = 0;
        var resultCount = 0;

        if (stage is null or ReviewStages.Search)
        {
            var rows = await LoadRowsAsync(kind, EnrichmentJobStatus.AwaitingSearchApproval, ct);
            searchCount = rows.Count;
            var twinNames = await FindTwinNamesAsync(rows, ct);
            items.AddRange(rows.Select(r =>
            {
                var threshold = thresholds.QueryMin(DomainOf(r.Kind));
                return new ReviewInboxItemDto(
                    r.Id, r.Kind, ReviewStages.Search, r.Name, r.Specimen,
                    string.IsNullOrWhiteSpace(r.ProposedQuery) ? r.NormalizedName : r.ProposedQuery,
                    r.QueryConfidence, r.QueryConfidenceReason, threshold,
                    !EnrichmentReviewThresholds.IsConfident(r.QueryConfidence, threshold), r.Origin, r.CreatedAt,
                    twinNames.Contains((r.Kind, r.Id)), r.HasNote);
            }));
        }

        if (stage is null or ReviewStages.Result)
        {
            var rows = await LoadRowsAsync(kind, EnrichmentJobStatus.AwaitingResultReview, ct);
            resultCount = rows.Count;
            items.AddRange(rows.Select(r =>
            {
                var threshold = thresholds.ResultMin(DomainOf(r.Kind));
                return new ReviewInboxItemDto(
                    r.Id, r.Kind, ReviewStages.Result, r.Name, r.Specimen, null,
                    r.ResultConfidence, r.ResultConfidenceReason, threshold,
                    !EnrichmentReviewThresholds.IsConfident(r.ResultConfidence, threshold), r.Origin, r.CreatedAt, false, r.HasNote);
            }));
        }

        items = items.OrderByDescending(i => i.BelowThreshold).ThenBy(i => i.CreatedAt).ToList();
        return new ReviewInboxResponse(items.Skip(skip).Take(take).ToList(), items.Count, searchCount, resultCount);
    }

    /// <summary>Строки поисков показателей, у которых есть «двойники» — свежий кэш того же названия у другой группы
    /// биоматериалов. Одним запросом на всю выдачу, не по запросу на строку.</summary>
    private async Task<HashSet<(string Kind, Guid Id)>> FindTwinNamesAsync(List<QueueRow> rows, CancellationToken ct)
    {
        var lab = rows.Where(r => r.Kind == ReviewKinds.LabAnalyte && r.SpecimenKbId is not null).ToList();
        if (lab.Count == 0) return [];

        var names = lab.Select(r => r.NormalizedName).Distinct().ToList();
        var now = DateTime.UtcNow;
        var freshCaches = await db.LabAnalyteSearchCaches.AsNoTracking()
            .Where(c => names.Contains(c.NormalizedName) && c.CanBeUpdatedAfter > now && c.SnippetsJson != null && c.SnippetsJson != "[]")
            .Select(c => new { c.NormalizedName, c.SearchGroupKey })
            .ToListAsync(ct);
        if (freshCaches.Count == 0) return [];

        var specimenIds = lab.Select(r => r.SpecimenKbId!.Value).Distinct().ToList();
        var groups = await db.GlobalSpecimensKb.AsNoTracking()
            .Where(s => specimenIds.Contains(s.Id)).Select(s => new { s.Id, s.SearchGroupKey })
            .ToDictionaryAsync(s => s.Id, s => s.SearchGroupKey, ct);

        var result = new HashSet<(string, Guid)>();
        foreach (var r in lab)
        {
            var ownKey = SearchGroupKeys.Effective(r.SpecimenKbId!.Value, groups.GetValueOrDefault(r.SpecimenKbId.Value));
            if (freshCaches.Any(c => c.NormalizedName == r.NormalizedName && c.SearchGroupKey != ownKey))
                result.Add((r.Kind, r.Id));
        }

        return result;
    }

    /// <summary>Список поисков (GET /searches) — тот же Inbox, отфильтрованный по стадии.</summary>
    public async Task<ReviewSearchListResponse> ListSearchesAsync(string? kind, int skip, int take, CancellationToken ct = default)
    {
        var inbox = await ListInboxAsync(kind, ReviewStages.Search, skip, take, ct);
        return new ReviewSearchListResponse(
            inbox.Rows.Select(i => new ReviewSearchItemDto(
                i.Id, i.Kind, i.Name, i.Specimen, i.QueryText ?? string.Empty, i.Confidence, i.ConfidenceReason, i.Threshold,
                i.BelowThreshold, i.Origin, i.CreatedAt)).ToList(),
            inbox.Total);
    }

    /// <summary>Список результатов (GET /results) — тот же Inbox, отфильтрованный по стадии.</summary>
    public async Task<ReviewResultListResponse> ListResultsAsync(string? kind, int skip, int take, CancellationToken ct = default)
    {
        var inbox = await ListInboxAsync(kind, ReviewStages.Result, skip, take, ct);
        var providers = new Dictionary<Guid, string?>();
        foreach (var p in await db.MedicationEnrichmentJobs.AsNoTracking()
                     .Where(j => j.Status == EnrichmentJobStatus.AwaitingResultReview).Select(j => new { j.Id, j.Provider }).ToListAsync(ct))
            providers[p.Id] = p.Provider;
        foreach (var p in await db.VisitMedicationEnrichmentJobs.AsNoTracking()
                     .Where(j => j.Status == EnrichmentJobStatus.AwaitingResultReview).Select(j => new { j.Id, j.Provider }).ToListAsync(ct))
            providers[p.Id] = p.Provider;
        foreach (var p in await db.LabAnalyteEnrichmentJobs.AsNoTracking()
                     .Where(j => j.Status == EnrichmentJobStatus.AwaitingResultReview).Select(j => new { j.Id, j.Provider }).ToListAsync(ct))
            providers[p.Id] = p.Provider;
        return new ReviewResultListResponse(
            inbox.Rows.Select(i => new ReviewResultItemDto(
                i.Id, i.Kind, i.Name, i.Specimen, i.Confidence, i.ConfidenceReason, i.Threshold, i.BelowThreshold,
                providers.GetValueOrDefault(i.Id), i.CreatedAt)).ToList(),
            inbox.Total);
    }

    private static EnrichmentReviewDomain DomainOf(string kind) =>
        kind == ReviewKinds.LabAnalyte ? EnrichmentReviewDomain.Analyte : EnrichmentReviewDomain.Medication;

    // ------------------------------------------------------------------ деталь задачи

    private static readonly string[] MedicationCheckedFields =
        ["internationalName", "tradeNames", "form", "purpose", "simplePurpose", "usage", "storage", "driving", "specialNotes"];

    private static readonly string[] LabCheckedFields =
        ["loincCode", "defaultUnit", "plainExplanation", "whyMeasured", "highMeans", "lowMeans", "calculationInstructions", "refRanges"];

    public async Task<ReviewItemDetailDto?> GetItemAsync(string kind, Guid id, CancellationToken ct = default)
    {
        var job = await LoadAsync(kind, id, ct, tracking: false);
        if (job is null || !job.Status.IsAwaitingAdmin()) return null;

        var stage = job.Status == EnrichmentJobStatus.AwaitingSearchApproval ? ReviewStages.Search : ReviewStages.Result;
        var thresholds = await config.GetAsync(ct);
        var domain = DomainOf(kind);
        var queryThreshold = thresholds.QueryMin(domain);
        var resultThreshold = thresholds.ResultMin(domain);

        string? specimen = null;
        string origin;
        Guid? specimenId = null;
        if (job is LabAnalyteEnrichmentJob labJob)
        {
            specimenId = labJob.SpecimenKbId;
            specimen = await db.GlobalSpecimensKb.AsNoTracking()
                .Where(s => s.Id == labJob.SpecimenKbId).Select(s => s.DisplayName).FirstOrDefaultAsync(ct);
            origin = LabOriginCode(labJob.Origin);
        }
        else
        {
            origin = kind == ReviewKinds.Medication ? "medkit" : "visit";
        }

        ReviewDraftDto? draftDto = null;
        ReviewCurrentKbDto? current = null;
        ReviewFieldSourcesDto? fieldInfo = null;
        IReadOnlyList<DraftSnippet> draftSnippets = [];
        var usedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (stage == ReviewStages.Result && !string.IsNullOrEmpty(job.DraftPayloadJson))
        {
            if (kind == ReviewKinds.LabAnalyte)
            {
                var draft = EnrichmentDraftSerializer.Deserialize<LabAnalyteDraft>(job.DraftPayloadJson)!;
                draftDto = new ReviewDraftDto(
                    draft.NormalizedName, LabAnalyteNameCleaner.Clean(draft.DisplayName), LabAnalyteKbPayload.Build(draft.Summary),
                    LabAnalyteKbWriter.BuildAliases(draft.NormalizedName, draft.Summary).ToList(), draft.Source);
                var existing = await catalog.GetLabAnalyteByKeyAsync(draft.NormalizedName, draft.SpecimenKbId, ct);
                current = existing is null ? null : ToCurrent(existing);
                draftSnippets = draft.Snippets;
                foreach (var i in draft.Summary.UsedSourceIndexes.Where(i => i >= 0 && i < draftSnippets.Count))
                    usedUrls.Add(draftSnippets[i].Url);
                fieldInfo = BuildFieldSourceInfo(LabCheckedFields, draftDto.PayloadJson, draft.FieldSources, draftSnippets, draft.Summary);
            }
            else
            {
                var draft = EnrichmentDraftSerializer.Deserialize<MedicationDraft>(job.DraftPayloadJson)!;
                draftDto = new ReviewDraftDto(
                    draft.NormalizedName, draft.DisplayName, KbWriter.BuildPayloadJson(draft.Summary),
                    KbWriter.BuildAliases(draft.NormalizedName, draft.Summary, draft.ExtraAliases).ToList(), draft.Source);
                var existing = await catalog.GetMedicationByNormalizedNameAsync(draft.NormalizedName, ct);
                current = existing is null ? null : ToCurrent(existing);
                draftSnippets = draft.Snippets;
                foreach (var i in draft.Summary.UsedSourceIndexes.Where(i => i >= 0 && i < draftSnippets.Count))
                    usedUrls.Add(draftSnippets[i].Url);
                fieldInfo = BuildFieldSourceInfo(MedicationCheckedFields, draftDto.PayloadJson, draft.FieldSources, draftSnippets, null);
            }
        }

        var (cacheRow, groupKey, queryLabel) = await FindCacheAsync(job, ct);
        var topic = ReviewKinds.TopicOf(kind);
        var sources = await BuildSourcesAsync(topic, cacheRow, usedUrls, ct);
        // Нет строки кэша, но есть снимок сниппетов черновика (например, кэш удалили) — показываем снимок только для чтения.
        if (cacheRow is null && draftSnippets.Count > 0)
        {
            var snapshotDomains = await trustedDomains.GetActiveDomainsByPriorityAsync(topic, ct);
            sources = draftSnippets.Select((s, i) => new ReviewSourceDto(
                i, s.Title, s.Url, s.Text, SnippetKinds.IsExpertUrl(s.Url) ? SnippetKinds.ExpertSourceLabel : HostOf(s.Url),
                EnrichmentSnippetFilter.IsTrustedDomain(s.Url, snapshotDomains), true, null, s.Origin, s.Kind, null, false,
                usedUrls.Contains(s.Url), false)).ToList();
        }

        var twins = new List<ReviewTwinDto>();
        if (stage == ReviewStages.Search && specimenId is { } sid)
        {
            twins = (await labCache.FindTwinsAsync(job.NormalizedName, sid, ct))
                .Select(t => new ReviewTwinDto(t.CacheId, t.SpecimenDisplayName, t.SearchGroupKey, t.Provider, t.LastUpdatedAt, t.SnippetCount))
                .ToList();
        }

        var queryText = EnrichmentReviewGate.EffectiveQuery(job);
        return new ReviewItemDetailDto(
            job.Id, kind, stage, job.SourceDisplayName, specimen, origin, job.Provider, job.CreatedAt,
            queryText, job.QueryConfidence, job.QueryConfidenceReason, queryThreshold,
            !EnrichmentReviewThresholds.IsConfident(job.QueryConfidence, queryThreshold),
            job.ResultConfidence, job.ResultConfidenceReason, resultThreshold,
            !EnrichmentReviewThresholds.IsConfident(job.ResultConfidence, resultThreshold),
            draftDto, current, fieldInfo, ToCacheDto(topic, cacheRow, groupKey, queryLabel), sources, twins, job.ReviewNote);
    }

    private static ReviewCurrentKbDto ToCurrent(AdminLabAnalyteDetail d) => new(
        d.Id, d.DisplayName, d.PayloadJson, d.Aliases.ToList(), d.LockedFields.ToList(), d.UpdatedAt,
        d.VerificationStatus, d.VerifiedAt, d.VerificationStale);

    private static ReviewCurrentKbDto ToCurrent(AdminMedicationDetail d) => new(
        d.Id, d.DisplayName, d.PayloadJson, d.Aliases.ToList(), d.LockedFields.ToList(), d.UpdatedAt,
        d.VerificationStatus, d.VerifiedAt, d.VerificationStale);

    /// <summary>Атрибуция полей к источникам (ADR-0018). Поле — «без источника», если оно непустое, а модель не указала
    /// ни одного сниппета (для refRanges — ни у одного диапазона нет sourceIndex). Если модель вообще не вернула атрибуцию
    /// (Available=false), подсвечивать нечего — иначе подсвечивалось бы всё.</summary>
    public static ReviewFieldSourcesDto BuildFieldSourceInfo(
        string[] checkedFields, string payloadJson, IReadOnlyDictionary<string, List<int>>? fieldSources,
        IReadOnlyList<DraftSnippet> snippets, LabAnalyteSummary? labSummary)
    {
        var map = new Dictionary<string, List<string>>();
        if (fieldSources is not null)
        {
            foreach (var (field, indexes) in fieldSources)
            {
                var urls = indexes.Where(i => i >= 0 && i < snippets.Count).Select(i => snippets[i].Url).Distinct().ToList();
                if (urls.Count > 0) map[field] = urls;
            }
        }

        if (labSummary is not null)
        {
            var rangeUrls = labSummary.RefRanges
                .Select(r => r.SourceIndex)
                .Where(i => i is >= 0 && i < snippets.Count)
                .Select(i => snippets[i!.Value].Url).Distinct().ToList();
            if (rangeUrls.Count > 0) map["refRanges"] = rangeUrls;
        }

        var available = map.Count > 0;
        var without = new List<string>();
        if (available)
        {
            JsonObject? payload = null;
            try { payload = JsonNode.Parse(payloadJson) as JsonObject; } catch (JsonException) { }
            foreach (var field in checkedFields)
            {
                if (payload is null || !payload.TryGetPropertyValue(field, out var value) || !HasContent(value)) continue;
                if (!map.ContainsKey(field)) without.Add(field);
            }
        }

        return new ReviewFieldSourcesDto(available, map, without);
    }

    private static bool HasContent(JsonNode? node) => node switch
    {
        null => false,
        JsonArray arr => arr.Count > 0,
        JsonValue v when v.TryGetValue<string>(out var s) => !string.IsNullOrWhiteSpace(s),
        _ => true,
    };

    private async Task<(ISearchCacheRow? Row, string? GroupKey, string? QueryLabel)> FindCacheAsync(
        IReviewableEnrichmentJob job, CancellationToken ct)
    {
        if (job is LabAnalyteEnrichmentJob lab)
        {
            var group = await labCache.GetSearchGroupAsync(lab.SpecimenKbId, ct);
            return (await labCache.GetByNameAsync(lab.NormalizedName, lab.SpecimenKbId, ct), group.GroupKey, group.QueryLabel);
        }

        return (await medCache.GetByNameAsync(job.NormalizedName, ct), null, null);
    }

    private static ReviewCacheDto ToCacheDto(WebSearchTopic topic, ISearchCacheRow? row, string? groupKey, string? queryLabel)
    {
        var topicCode = topic == WebSearchTopic.LabAnalyte ? "lab-analyte" : "medication";
        if (row is null) return new ReviewCacheDto(null, topicCode, null, null, null, false, 0, 0, groupKey, queryLabel);

        var snippets = SearchCacheSnippets.Parse(row.SnippetsJson);
        return new ReviewCacheDto(
            row.Id, topicCode, row.Provider, row.LastUpdatedAt, row.CanBeUpdatedAfter, row.CanBeUpdatedAfter > DateTime.UtcNow,
            snippets.Count, snippets.Count(s => s.Origin == SnippetOrigin.Manual), groupKey, queryLabel);
    }

    private async Task<List<ReviewSourceDto>> BuildSourcesAsync(
        WebSearchTopic topic, ISearchCacheRow? row, IReadOnlySet<string> usedUrls, CancellationToken ct)
    {
        if (row is null) return [];

        var domains = await trustedDomains.GetActiveDomainsByPriorityAsync(topic, ct);
        var snippets = SearchCacheSnippets.Parse(row.SnippetsJson);
        var overrides = SearchCacheSnippets.ParseOverrides(row.OverridesJson);

        return snippets.Select((s, i) =>
        {
            var trusted = EnrichmentSnippetFilter.IsTrustedDomain(s.Url, domains);
            var enabled = EnrichmentSnippetFilter.IsEnabled(s, domains, overrides);
            bool? ov = overrides.TryGetValue(s.Url, out var flag) ? flag : null;
            var expert = SnippetKinds.IsExpertUrl(s.Url);
            return new ReviewSourceDto(
                i, s.Title, s.Url, s.Text, expert ? SnippetKinds.ExpertSourceLabel : HostOf(s.Url), trusted, enabled, ov,
                s.Origin, s.Kind, s.Note, s.Pinned, usedUrls.Contains(s.Url),
                UntrustedWarning: !expert && !trusted && (s.Origin == SnippetOrigin.Manual || s.Pinned));
        }).ToList();
    }

    private static string? HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;

    // ------------------------------------------------------------------ правка набора сниппетов

    /// <summary>Гарантирует строку кэша для задачи (создаёт пустую «устаревшую», если её ещё нет) — нужна, чтобы
    /// админ мог добавить ручной сниппет ещё на стадии поиска, до первого платного запроса.</summary>
    public async Task<ReviewActionOutcome<Guid>> EnsureCacheAsync(string kind, Guid id, CancellationToken ct = default)
    {
        var job = await LoadAsync(kind, id, ct, tracking: false);
        if (job is null) return new ReviewActionOutcome<Guid>(ReviewActionResult.NotFound);

        var row = job is LabAnalyteEnrichmentJob lab
            ? (ISearchCacheRow)await labCache.GetOrCreateAsync(lab.NormalizedName, lab.SpecimenKbId, ct)
            : await medCache.GetOrCreateAsync(job.NormalizedName, ct);

        // Кэша не было, а у задачи есть черновик: наполняем новую строку снимком его сниппетов. Иначе пустая строка
        // заменила бы в карточке снимок черновика, и «Пересуммировать» после добавления своего источника потерял бы
        // все исходные. Уже лежащие в строке (ручные) сниппеты сохраняются.
        if (!string.IsNullOrEmpty(job.DraftPayloadJson) && SearchCacheSnippets.Parse(row.SnippetsJson).Count == 0)
        {
            IReadOnlyList<DraftSnippet> draftSnippets;
            string source;
            if (job is LabAnalyteEnrichmentJob)
            {
                var draft = EnrichmentDraftSerializer.Deserialize<LabAnalyteDraft>(job.DraftPayloadJson);
                draftSnippets = draft?.Snippets ?? [];
                source = draft?.Source ?? string.Empty;
            }
            else
            {
                var draft = EnrichmentDraftSerializer.Deserialize<MedicationDraft>(job.DraftPayloadJson);
                draftSnippets = draft?.Snippets ?? [];
                source = draft?.Source ?? string.Empty;
            }

            if (draftSnippets.Count > 0)
            {
                // Подпись источника «brave: vidal.ru» → провайдер «brave».
                var provider = source.Split(':')[0].Trim() is { Length: > 0 } p ? p : null;
                var snippets = EnrichmentDraftSerializer.ToWebSnippets(draftSnippets);
                if (job is LabAnalyteEnrichmentJob) await labCache.UpdateAsync(row.Id, provider, snippets, ct);
                else await medCache.UpdateAsync(row.Id, provider, snippets, ct);
            }
        }

        return new ReviewActionOutcome<Guid>(ReviewActionResult.Ok, row.Id);
    }

    private static ReviewActionOutcome<WebSnippet> ToOutcome(CacheEditOutcome o) => o.Result switch
    {
        CacheEditResult.Ok => new ReviewActionOutcome<WebSnippet>(ReviewActionResult.Ok, o.Snippet),
        CacheEditResult.NotFound => new ReviewActionOutcome<WebSnippet>(ReviewActionResult.NotFound),
        _ => new ReviewActionOutcome<WebSnippet>(ReviewActionResult.Invalid, null, o.Error),
    };

    public async Task<ReviewActionOutcome<WebSnippet>> AddManualSnippetAsync(
        WebSearchTopic topic, Guid cacheId, AddManualSnippetRequest request, CancellationToken ct = default) =>
        ToOutcome(await cacheEditor.AddManualAsync(topic, cacheId, request.Kind, request.Url, request.Title, request.Text, request.Note, ct));

    public async Task<ReviewActionOutcome<WebSnippet>> RemoveSnippetAsync(
        WebSearchTopic topic, Guid cacheId, string url, CancellationToken ct = default) =>
        ToOutcome(await cacheEditor.RemoveAsync(topic, cacheId, url, ct));

    public async Task<ReviewActionOutcome<WebSnippet>> SetSnippetOverrideAsync(
        WebSearchTopic topic, Guid cacheId, string url, bool? enabled, CancellationToken ct = default) =>
        ToOutcome(await cacheEditor.SetOverrideAsync(topic, cacheId, url, enabled, ct));

    public async Task<ReviewActionOutcome<WebSnippet>> SetSnippetPinnedAsync(
        WebSearchTopic topic, Guid cacheId, string url, bool pinned, CancellationToken ct = default) =>
        ToOutcome(await cacheEditor.SetPinnedAsync(topic, cacheId, url, pinned, ct));

    public async Task<ReviewActionOutcome<WebSnippet>> EditSnippetAsync(
        WebSearchTopic topic, Guid cacheId, EditSnippetRequest request, CancellationToken ct = default) =>
        ToOutcome(await cacheEditor.EditAsync(topic, cacheId, request.Url, request.Title, request.Text, request.Note, ct));

    // ------------------------------------------------------------------ «взять из готового кэша»

    private const int MaxCandidates = 20;
    private const int CandidatesPerVariant = 50;

    /// <summary>Кандидаты «взять из готового кэша»: непустые строки кэша той же темы, чьё нормализованное имя содержит
    /// запрос (в сыром виде и после нормализатора темы — у показателей ключ свёрнут транслитерацией). Без запроса —
    /// имя задачи целиком и его отдельные слова (≥ 4 букв): так находятся «парацетамол» для «парацетамол экстра» и т.п.
    /// Собственная строка задачи исключается; точные совпадения запроса — первыми, дальше — свежие раньше.</summary>
    public async Task<List<ReviewCacheCandidateDto>?> FindCacheCandidatesAsync(
        string kind, Guid id, string? query, CancellationToken ct = default)
    {
        var job = await LoadAsync(kind, id, ct, tracking: false);
        if (job is null) return null;

        var topic = ReviewKinds.TopicOf(kind);
        var (ownRow, _, _) = await FindCacheAsync(job, ct);
        Func<string, string> normalize = topic == WebSearchTopic.LabAnalyte
            ? LabAnalyteNormalizer.NormalizeAnalyteKey
            : MedicationNameNormalizer.Normalize;

        var variants = new List<string>();
        void AddVariant(string? v)
        {
            v = v?.Trim();
            if (!string.IsNullOrEmpty(v) && !variants.Contains(v)) variants.Add(v);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            AddVariant(query.Trim().ToLowerInvariant().Replace('ё', 'е'));
            AddVariant(normalize(query));
        }
        else
        {
            AddVariant(job.NormalizedName);
            foreach (var word in job.NormalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4))
                AddVariant(word);
        }

        if (variants.Count == 0) return [];

        var rows = new Dictionary<Guid, ISearchCacheRow>();
        foreach (var v in variants.Take(6))
        {
            IEnumerable<ISearchCacheRow> found = topic == WebSearchTopic.LabAnalyte
                ? await db.LabAnalyteSearchCaches.AsNoTracking()
                    .Where(c => c.NormalizedName.Contains(v) && c.SnippetsJson != null && c.SnippetsJson != "[]")
                    .OrderByDescending(c => c.LastUpdatedAt).Take(CandidatesPerVariant).ToListAsync(ct)
                : await db.MedicationSearchCaches.AsNoTracking()
                    .Where(c => c.NormalizedName.Contains(v) && c.SnippetsJson != null && c.SnippetsJson != "[]")
                    .OrderByDescending(c => c.LastUpdatedAt).Take(CandidatesPerVariant).ToListAsync(ct);
            foreach (var r in found) rows.TryAdd(r.Id, r);
        }

        if (ownRow is not null) rows.Remove(ownRow.Id);

        var specimenNames = new Dictionary<Guid, string>();
        if (topic == WebSearchTopic.LabAnalyte)
        {
            var specimenIds = rows.Values.OfType<LabAnalyteSearchCache>().Select(r => r.SpecimenKbId).Distinct().ToList();
            specimenNames = await db.GlobalSpecimensKb.AsNoTracking()
                .Where(s => specimenIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.DisplayName, ct);
        }

        var now = DateTime.UtcNow;
        var primary = variants[0];
        var topicCode = topic == WebSearchTopic.LabAnalyte ? "lab-analyte" : "medication";
        return rows.Values
            .Select(r => (Row: r, Snippets: SearchCacheSnippets.Parse(r.SnippetsJson)))
            .Where(x => x.Snippets.Count > 0)
            .OrderBy(x => x.Row.NormalizedName == primary ? 0 : x.Row.NormalizedName.Contains(primary) ? 1 : 2)
            .ThenByDescending(x => x.Row.LastUpdatedAt)
            .Take(MaxCandidates)
            .Select(x => new ReviewCacheCandidateDto(
                x.Row.Id, topicCode, x.Row.NormalizedName,
                x.Row is LabAnalyteSearchCache lab ? specimenNames.GetValueOrDefault(lab.SpecimenKbId, lab.SpecimenKbId.ToString()) : null,
                x.Row.Provider, x.Row.LastUpdatedAt, x.Row.CanBeUpdatedAfter > now,
                x.Snippets.Count, x.Snippets.Count(s => s.Origin == SnippetOrigin.Manual)))
            .ToList();
    }

    /// <summary>Импорт выбранных сниппетов из готового кэша в набор задачи (строка задачи создаётся при необходимости,
    /// см. <see cref="EnsureCacheAsync"/>). Дальше админ правит набор и принимает его вместо платного поиска
    /// (одобрение поиска с mode=use-cache) либо пересуммирует черновик на стадии результата.</summary>
    public async Task<ReviewActionOutcome<ImportReviewCacheResponse>> ImportCacheAsync(
        string kind, Guid id, ImportReviewCacheRequest request, CancellationToken ct = default)
    {
        var job = await LoadAsync(kind, id, ct, tracking: false);
        if (job is null) return new ReviewActionOutcome<ImportReviewCacheResponse>(ReviewActionResult.NotFound);
        if (!job.Status.IsAwaitingAdmin())
            return new ReviewActionOutcome<ImportReviewCacheResponse>(ReviewActionResult.WrongStatus, null, "Задача уже обработана.");

        var ensured = await EnsureCacheAsync(kind, id, ct);
        if (ensured.Result != ReviewActionResult.Ok) return new ReviewActionOutcome<ImportReviewCacheResponse>(ensured.Result);

        var outcome = await cacheEditor.ImportFromAsync(ReviewKinds.TopicOf(kind), ensured.Value, request.SourceCacheId, request.Urls, ct);
        return outcome.Result switch
        {
            CacheEditResult.Ok => new ReviewActionOutcome<ImportReviewCacheResponse>(
                ReviewActionResult.Ok, new ImportReviewCacheResponse(ensured.Value, outcome.ImportedCount)),
            CacheEditResult.NotFound => new ReviewActionOutcome<ImportReviewCacheResponse>(ReviewActionResult.NotFound),
            _ => new ReviewActionOutcome<ImportReviewCacheResponse>(ReviewActionResult.Invalid, null, outcome.Error),
        };
    }

    public async Task<ReviewActionOutcome> SetNoteAsync(string kind, Guid id, string? note, CancellationToken ct = default)
    {
        var job = await LoadAsync(kind, id, ct);
        if (job is null) return new ReviewActionOutcome(ReviewActionResult.NotFound);
        if (note is { Length: > MaxNoteLength })
            return new ReviewActionOutcome(ReviewActionResult.Invalid, $"Заметка длиннее {MaxNoteLength} символов.");

        job.ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        await db.SaveChangesAsync(ct);
        return ReviewActionOutcome.Ok;
    }

    // ------------------------------------------------------------------ гейт 1: поиски

    public async Task<ReviewActionOutcome> ApproveSearchAsync(
        string kind, Guid id, ApproveSearchRequest request, CancellationToken ct = default)
    {
        var job = await LoadAsync(kind, id, ct);
        if (job is null) return new ReviewActionOutcome(ReviewActionResult.NotFound);
        if (job.Status != EnrichmentJobStatus.AwaitingSearchApproval)
            return new ReviewActionOutcome(ReviewActionResult.WrongStatus, "Задача уже обработана.");

        var mode = request.Mode ?? ModeSearch;
        if (mode is not (ModeSearch or ModeUseCache))
            return new ReviewActionOutcome(ReviewActionResult.Invalid, "mode: search или use-cache.");
        if (request.Note is { Length: > MaxNoteLength })
            return new ReviewActionOutcome(ReviewActionResult.Invalid, $"Заметка длиннее {MaxNoteLength} символов.");

        string? query = null;
        if (request.QueryText is not null)
        {
            query = request.QueryText.Trim();
            if (query.Length == 0) return new ReviewActionOutcome(ReviewActionResult.Invalid, "Текст запроса не может быть пустым.");
            if (query.Length > MaxQueryLength)
                return new ReviewActionOutcome(ReviewActionResult.Invalid, $"Текст запроса длиннее {MaxQueryLength} символов.");
            // Правка админа уходит наружу в платный провайдер (ADR-0005 — egress только названий):
            // тот же гейт на персональный контекст, что у записи в справочник.
            var violation = KbIsolationGuard.FindViolation([query]);
            if (violation is not null)
                return new ReviewActionOutcome(ReviewActionResult.Invalid, $"Подозрение на персональный контекст: {violation}");
        }

        if (mode == ModeUseCache)
        {
            var prepared = await PrepareCacheUseAsync(job, request.TwinCacheId, ct);
            if (prepared.Result != ReviewActionResult.Ok) return prepared;
        }

        if (query is not null) job.ProposedQueryText = query;
        if (!string.IsNullOrWhiteSpace(request.Note)) job.ReviewNote = request.Note.Trim();
        job.SearchApprovedAt = DateTime.UtcNow;
        job.ReviewedAt = DateTime.UtcNow;
        job.Status = EnrichmentJobStatus.Pending;
        job.Error = null;
        // Попытка, потраченная на «парковку», не должна сокращать ретраи реального прогона.
        job.Attempts = 0;

        // Pending-строка и Hangfire-энкью — единая единица отката (тот же приём, что
        // EnrichmentRequestService.EnqueueAsync): сбой энкью не должен оставить Pending без задачи в очереди.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        AdminPipelineEndpoints.EnqueueByType[kind](backgroundJobs, job.Id);
        await tx.CommitAsync(ct);

        logger.LogInformation("Одобрен {Mode}-поиск {Kind} {JobId} «{Name}», запрос «{Query}».",
            mode, kind, job.Id, job.SourceDisplayName, EnrichmentReviewGate.EffectiveQuery(job));
        return ReviewActionOutcome.Ok;
    }

    /// <summary>Режим «использовать кэш»: делает строку кэша задачи свежей, чтобы процессор взял сниппеты из неё и не
    /// пошёл в платный поиск. С twinCacheId — сначала копирует набор «двойника» (только показатели).</summary>
    private async Task<ReviewActionOutcome> PrepareCacheUseAsync(IReviewableEnrichmentJob job, Guid? twinCacheId, CancellationToken ct)
    {
        var topic = job is LabAnalyteEnrichmentJob ? WebSearchTopic.LabAnalyte : WebSearchTopic.Medication;
        ISearchCacheRow row;
        if (job is LabAnalyteEnrichmentJob lab)
        {
            row = await labCache.GetOrCreateAsync(lab.NormalizedName, lab.SpecimenKbId, ct);
            if (twinCacheId is { } twinId)
            {
                var copied = await cacheEditor.CopyFromAsync(row.Id, twinId, ct);
                if (copied.Result != CacheEditResult.Ok)
                    return new ReviewActionOutcome(
                        copied.Result == CacheEditResult.NotFound ? ReviewActionResult.NotFound : ReviewActionResult.Invalid, copied.Error);
            }
        }
        else
        {
            if (twinCacheId is not null)
                return new ReviewActionOutcome(ReviewActionResult.Invalid, "У препаратов нет двойников по биоматериалу.");
            row = await medCache.GetOrCreateAsync(job.NormalizedName, ct);
        }

        // Перечитываем: строка могла измениться в редакторе (копия/ручные правки).
        await db.Entry(row).ReloadAsync(ct);
        var enabled = await EnabledSnippetCountAsync(topic, row, ct);
        if (enabled == 0)
            return new ReviewActionOutcome(
                ReviewActionResult.Invalid,
                "В кэше нет включённых сниппетов — включите источник, добавьте свой или выберите платный поиск.");

        var marked = await cacheEditor.MarkFreshAsync(topic, row.Id, options.Value.MinRefreshIntervalMonths, ct);
        return marked.Result == CacheEditResult.Ok ? ReviewActionOutcome.Ok : new ReviewActionOutcome(ReviewActionResult.NotFound);
    }

    private async Task<int> EnabledSnippetCountAsync(WebSearchTopic topic, ISearchCacheRow row, CancellationToken ct)
    {
        var domains = await trustedDomains.GetActiveDomainsByPriorityAsync(topic, ct);
        return EnrichmentSnippetFilter.SelectEnabled(
            SearchCacheSnippets.Parse(row.SnippetsJson), domains, SearchCacheSnippets.ParseOverrides(row.OverridesJson)).Count;
    }

    public async Task<ReviewActionOutcome> RejectSearchAsync(string kind, Guid id, string? reason, string? note, CancellationToken ct = default) =>
        await RejectAsync(kind, id, EnrichmentJobStatus.AwaitingSearchApproval, reason, note, "Платный поиск отклонён администратором.", ct);

    public async Task<BulkReviewResponse> BulkApproveSearchesAsync(IEnumerable<BulkApproveItem> items, CancellationToken ct = default)
    {
        var failed = new List<ReviewItemRef>();
        var processed = 0;
        foreach (var item in items.DistinctBy(i => (i.Kind, i.Id)))
        {
            var outcome = ReviewKinds.IsValid(item.Kind)
                ? await ApproveSearchAsync(item.Kind, item.Id, new ApproveSearchRequest(item.QueryText), ct)
                : new ReviewActionOutcome(ReviewActionResult.Invalid);
            if (outcome.Result == ReviewActionResult.Ok) processed++;
            else failed.Add(new ReviewItemRef(item.Kind, item.Id));
        }
        return new BulkReviewResponse(processed, failed);
    }

    public async Task<BulkReviewResponse> BulkRejectSearchesAsync(IEnumerable<ReviewItemRef> items, string? reason, CancellationToken ct = default)
    {
        var failed = new List<ReviewItemRef>();
        var processed = 0;
        foreach (var item in items.DistinctBy(i => (i.Kind, i.Id)))
        {
            var outcome = ReviewKinds.IsValid(item.Kind)
                ? await RejectSearchAsync(item.Kind, item.Id, reason, null, ct)
                : new ReviewActionOutcome(ReviewActionResult.Invalid);
            if (outcome.Result == ReviewActionResult.Ok) processed++;
            else failed.Add(item);
        }
        return new BulkReviewResponse(processed, failed);
    }

    // ------------------------------------------------------------------ гейт 2: результаты

    public async Task<ReviewActionOutcome> RejectResultAsync(string kind, Guid id, string? reason, string? note, CancellationToken ct = default) =>
        await RejectAsync(kind, id, EnrichmentJobStatus.AwaitingResultReview, reason, note, "Результат отклонён администратором.", ct);

    private async Task<ReviewActionOutcome> RejectAsync(
        string kind, Guid id, EnrichmentJobStatus expected, string? reason, string? note, string defaultMessage, CancellationToken ct)
    {
        var job = await LoadAsync(kind, id, ct);
        if (job is null) return new ReviewActionOutcome(ReviewActionResult.NotFound);
        if (job.Status != expected) return new ReviewActionOutcome(ReviewActionResult.WrongStatus, "Задача уже обработана.");
        if (note is { Length: > MaxNoteLength })
            return new ReviewActionOutcome(ReviewActionResult.Invalid, $"Заметка длиннее {MaxNoteLength} символов.");

        // Failed + RejectedByAdmin — смысловой отказ человека. Пользователя не уведомляем (это не
        // сбой системы), повторный автозапрос того же имени блокирует обычная проверка alreadyFailed
        // в *RequestService, пока админ сам не перезапустит.
        job.Status = EnrichmentJobStatus.Failed;
        job.FailureReason = EnrichmentFailureReason.RejectedByAdmin;
        job.Error = string.IsNullOrWhiteSpace(reason) ? defaultMessage : reason.Trim();
        job.IsTransientFailure = false;
        job.CompletedAt = DateTime.UtcNow;
        job.ReviewedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(note)) job.ReviewNote = note.Trim();
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Отклонено {Kind} {JobId} «{Name}» ({Stage}): {Reason}",
            kind, job.Id, job.SourceDisplayName, expected, job.Error);
        return ReviewActionOutcome.Ok;
    }

    /// <summary>Одобрение результата (с правками или без): запись в kb существующими писателями, затем — если админ
    /// что-то поправил — тем же путём, что ручная правка каталога (<see cref="AdminCatalogService"/>), поэтому
    /// правленые поля попадают в LockedFields. Без правок запись помечается проверенной (AdminVerified, либо
    /// ManualKnowledge, если черновик построен только на знании эксперта). Всё в одной транзакции вместе со сменой
    /// статуса и публикацией события.</summary>
    public async Task<ReviewActionOutcome> ApproveResultAsync(
        string kind, Guid id, ApproveResultRequest request, CancellationToken ct = default)
    {
        var job = await LoadAsync(kind, id, ct);
        if (job is null) return new ReviewActionOutcome(ReviewActionResult.NotFound);
        if (job.Status != EnrichmentJobStatus.AwaitingResultReview)
            return new ReviewActionOutcome(ReviewActionResult.WrongStatus, "Задача уже обработана.");
        if (string.IsNullOrEmpty(job.DraftPayloadJson))
            return new ReviewActionOutcome(ReviewActionResult.Invalid, "У задачи нет черновика.");
        if (request.PayloadJson is not null && !TryParseObjectOrAny(request.PayloadJson, out _))
            return new ReviewActionOutcome(ReviewActionResult.Invalid, "PayloadJson — невалидный JSON.");
        if (request.Note is { Length: > MaxNoteLength })
            return new ReviewActionOutcome(ReviewActionResult.Invalid, $"Заметка длиннее {MaxNoteLength} символов.");

        const string logNote = "Одобрено из очереди «Одобрение»";
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        Guid kbId;
        string draftPayload;
        string draftName;
        string[] draftAliases;
        Func<string, string> normalizeAlias;
        string finalDisplayName;
        bool expertOnly;
        var target = kind == ReviewKinds.LabAnalyte ? KbChangeTarget.LabAnalyteKb : KbChangeTarget.MedicationKb;

        if (kind == ReviewKinds.LabAnalyte)
        {
            var draft = EnrichmentDraftSerializer.Deserialize<LabAnalyteDraft>(job.DraftPayloadJson)!;
            var write = await labWriter.UpsertAsync(
                draft.NormalizedName, draft.SpecimenKbId, draft.DisplayName, draft.Summary, draft.Source, ct,
                KbChangeLogService.ActorAdmin, logNote);
            if (!write.Success)
            {
                await tx.RollbackAsync(ct);
                return new ReviewActionOutcome(ReviewActionResult.Invalid, write.RejectionReason);
            }

            kbId = write.KbId!.Value;
            draftPayload = LabAnalyteKbPayload.Build(draft.Summary);
            draftName = LabAnalyteNameCleaner.Clean(draft.DisplayName);
            draftAliases = LabAnalyteKbWriter.BuildAliases(draft.NormalizedName, draft.Summary);
            normalizeAlias = LabAnalyteNormalizer.NormalizeAnalyteKey;
            finalDisplayName = draftName;
            expertOnly = IsExpertOnly(draft.Snippets, draft.Summary.UsedSourceIndexes);
        }
        else
        {
            var draft = EnrichmentDraftSerializer.Deserialize<MedicationDraft>(job.DraftPayloadJson)!;
            var write = await kbWriter.UpsertAsync(
                draft.NormalizedName, draft.DisplayName, draft.Summary, draft.Source, draft.ExtraAliases, ct,
                KbChangeLogService.ActorAdmin, logNote);
            if (!write.Success)
            {
                await tx.RollbackAsync(ct);
                return new ReviewActionOutcome(ReviewActionResult.Invalid, write.RejectionReason);
            }

            kbId = write.KbId!.Value;
            draftPayload = KbWriter.BuildPayloadJson(draft.Summary);
            draftName = draft.DisplayName;
            draftAliases = KbWriter.BuildAliases(draft.NormalizedName, draft.Summary, draft.ExtraAliases);
            normalizeAlias = MedicationNameNormalizer.Normalize;
            finalDisplayName = draftName;
            expertOnly = IsExpertOnly(draft.Snippets, draft.Summary.UsedSourceIndexes);
        }

        var edit = BuildEdit(request, draftPayload, draftName, draftAliases, normalizeAlias);
        if (edit is not null)
        {
            var (result, reason) = kind == ReviewKinds.LabAnalyte
                ? await UpdateLabAnalyteAsync(kbId, edit, logNote, ct)
                : await UpdateMedicationAsync(kbId, edit, logNote, ct);
            if (result != AdminKbEditResult.Ok)
            {
                await tx.RollbackAsync(ct);
                var message = result switch
                {
                    AdminKbEditResult.InvalidPayloadJson => "PayloadJson — невалидный JSON.",
                    AdminKbEditResult.IsolationViolation => $"Подозрение на персональный контекст: {reason}",
                    _ => "Запись справочника не найдена.",
                };
                return new ReviewActionOutcome(ReviewActionResult.Invalid, message);
            }

            if (edit.DisplayName is not null) finalDisplayName = edit.DisplayName;
        }
        else
        {
            // Без правок админ подтвердил черновик как есть — запись проверена человеком.
            await catalog.MarkVerifiedAsync(
                target, kbId, expertOnly ? KbVerificationStatus.ManualKnowledge : KbVerificationStatus.AdminVerified, logNote, ct);
        }

        job.Status = EnrichmentJobStatus.Completed;
        job.KbId = kbId;
        job.Error = null;
        job.FailureReason = null;
        job.CompletedAt = DateTime.UtcNow;
        job.ReviewedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(request.Note)) job.ReviewNote = request.Note.Trim();

        if (kind == ReviewKinds.Medication && job is MedicationEnrichmentJob medicationJob)
        {
            var medkitId = await ResolveMedkitIdAsync(medicationJob.MedicationId, ct);
            await publisher.PublishAsync(new MedicationEnrichedEvent(
                job.Id, kbId, finalDisplayName, job.RequestedByUserId, medicationJob.FamilyId, medkitId), ct);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Дозаполнение задним числом — как в процессоре: показатели, распознанные до появления
        // статьи, застряли на RefSource.None. Постановка после коммита (Hangfire — отдельное соединение).
        if (kind == ReviewKinds.LabAnalyte)
            backgroundJobs.Enqueue<RecalculateIndicatorFlagsJob>(j => j.RunAsync(kbId, CancellationToken.None));

        logger.LogInformation("Одобрен результат {Kind} {JobId} «{Name}»{Edited}.",
            kind, job.Id, finalDisplayName, edit is null ? string.Empty : " с правками");
        return ReviewActionOutcome.Ok;
    }

    /// <summary>Черновик опирается только на знание эксперта (ручные сниппеты без веб-источников): все использованные
    /// моделью источники — expert-knowledge. Тогда запись получает статус ManualKnowledge.</summary>
    private static bool IsExpertOnly(IReadOnlyList<DraftSnippet> snippets, IReadOnlyList<int> usedIndexes)
    {
        var used = usedIndexes.Where(i => i >= 0 && i < snippets.Count).Select(i => snippets[i]).ToList();
        return used.Count > 0 && used.All(s => s.Kind == SnippetKinds.ExpertKnowledge);
    }

    private async Task<(AdminKbEditResult Result, string? Reason)> UpdateLabAnalyteAsync(
        Guid kbId, AdminKbEditRequest edit, string logNote, CancellationToken ct)
    {
        var (result, _, reason) = await catalog.UpdateLabAnalyteAsync(kbId, edit, ct, logNote);
        return (result, reason);
    }

    private async Task<(AdminKbEditResult Result, string? Reason)> UpdateMedicationAsync(
        Guid kbId, AdminKbEditRequest edit, string logNote, CancellationToken ct)
    {
        var (result, _, reason) = await catalog.UpdateMedicationAsync(kbId, edit, ct, logNote);
        return (result, reason);
    }

    /// <summary>Что админ реально поменял относительно черновика. Не изменённое поле остаётся null
    /// (AdminCatalogService его не трогает и не лочит); для payload лочатся только отличающиеся
    /// ключи верхнего уровня ("payload.&lt;key&gt;"), а не весь payload — как режим формы редактора.</summary>
    public static AdminKbEditRequest? BuildEdit(
        ApproveResultRequest request, string draftPayload, string draftName, string[] draftAliases,
        Func<string, string> normalizeAlias)
    {
        string? displayName = null;
        if (!string.IsNullOrWhiteSpace(request.DisplayName) && request.DisplayName.Trim() != draftName)
            displayName = request.DisplayName.Trim();

        string? payload = null;
        List<string>? lockedKeys = null;
        if (request.PayloadJson is not null && TryParseObjectOrAny(request.PayloadJson, out var editedNode))
        {
            var draftNode = JsonNode.Parse(draftPayload);
            if (!JsonNode.DeepEquals(editedNode, draftNode))
            {
                payload = request.PayloadJson;
                if (editedNode is JsonObject editedObj && draftNode is JsonObject draftObj)
                {
                    lockedKeys = editedObj.Select(p => p.Key).Concat(draftObj.Select(p => p.Key)).Distinct()
                        .Where(key =>
                        {
                            editedObj.TryGetPropertyValue(key, out var e);
                            draftObj.TryGetPropertyValue(key, out var d);
                            return !JsonNode.DeepEquals(e, d);
                        })
                        .ToList();
                }
            }
        }

        List<string>? aliases = null;
        if (request.Aliases is not null)
        {
            var edited = request.Aliases.Select(normalizeAlias).Where(a => a.Length > 0).ToHashSet();
            if (!edited.SetEquals(draftAliases)) aliases = request.Aliases;
        }

        return displayName is null && payload is null && aliases is null
            ? null
            : new AdminKbEditRequest(displayName, payload, aliases, lockedKeys);
    }

    private static bool TryParseObjectOrAny(string json, out JsonNode? node)
    {
        try
        {
            node = JsonNode.Parse(json);
            return node is not null;
        }
        catch (JsonException)
        {
            node = null;
            return false;
        }
    }

    // ------------------------------------------------------------------ пересуммаризация

    /// <summary>Набор для суммаризатора из ТЕКУЩЕГО состояния кэша (с учётом правок админа: включённые/выключенные,
    /// закреплённые, ручные) — тот же конвейер отбора, что у процессора.</summary>
    private async Task<List<WebSnippet>> SelectCurrentSnippetsAsync(WebSearchTopic topic, ISearchCacheRow row, CancellationToken ct)
    {
        var domains = await trustedDomains.GetActiveDomainsByPriorityAsync(topic, ct);
        return EnrichmentSnippetFilter.SelectForSummary(
            SearchCacheSnippets.Parse(row.SnippetsJson), domains, SearchCacheSnippets.ParseOverrides(row.OverridesJson),
            options.Value.MaxSnippets, rankOrder: topic == WebSearchTopic.LabAnalyte);
    }

    private const string NoSnippetsMessage = "В наборе нет включённых сниппетов — включите источник или добавьте свой.";

    /// <summary>Пересуммаризация «из тех же сниппетов» — без нового платного поиска. Набор берётся из кэша (с правками админа:
    /// ручные/закреплённые/выключенные), при отсутствии кэша — снимок черновика. Новый черновик заменяет старый, задача
    /// остаётся на ревью (даже при высокой уверенности: решение за админом).</summary>
    public async Task<ReviewActionOutcome> ResummarizeAsync(string kind, Guid id, CancellationToken ct = default)
    {
        var job = await LoadAsync(kind, id, ct);
        if (job is null) return new ReviewActionOutcome(ReviewActionResult.NotFound);
        if (job.Status != EnrichmentJobStatus.AwaitingResultReview || string.IsNullOrEmpty(job.DraftPayloadJson))
            return new ReviewActionOutcome(ReviewActionResult.WrongStatus, "Задача не ждёт ревью результата.");

        var providerName = job.Provider ?? "web";
        var topic = ReviewKinds.TopicOf(kind);
        var (cacheRow, _, _) = await FindCacheAsync(job, ct);

        if (kind == ReviewKinds.LabAnalyte)
        {
            var draft = EnrichmentDraftSerializer.Deserialize<LabAnalyteDraft>(job.DraftPayloadJson)!;
            var snippets = cacheRow is null
                ? EnrichmentDraftSerializer.ToWebSnippets(draft.Snippets).ToList()
                : await SelectCurrentSnippetsAsync(topic, cacheRow, ct);
            if (snippets.Count == 0) return new ReviewActionOutcome(ReviewActionResult.Invalid, NoSnippetsMessage);

            var summarized = await labSummarizer.SummarizeAsync(job.SourceDisplayName, snippets, ct);
            if (!summarized.Success || summarized.Summary is null)
                return new ReviewActionOutcome(ReviewActionResult.UpstreamFailed, summarized.Error ?? "Модель не вернула ответ.");

            var domains = await trustedDomains.GetActiveDomainsByPriorityAsync(WebSearchTopic.LabAnalyte, ct);
            var merged = ReferenceRangeMerger.Merge(summarized.Summary.RefRanges, snippets, domains);
            var summary = summarized.Summary with { RefRanges = merged };
            var source = EnrichmentReviewGate.BuildSourceLabel(providerName, snippets, summary.UsedSourceIndexes);
            job.DraftPayloadJson = EnrichmentDraftSerializer.Serialize(new LabAnalyteDraft(
                draft.NormalizedName, draft.SpecimenKbId, draft.DisplayName, source, summary,
                EnrichmentDraftSerializer.ToDraftSnippets(snippets), summarized.FieldSources));
            job.ResultConfidence = summarized.Confidence;
            job.ResultConfidenceReason = summarized.ConfidenceReason;
        }
        else
        {
            var draft = EnrichmentDraftSerializer.Deserialize<MedicationDraft>(job.DraftPayloadJson)!;
            var snippets = cacheRow is null
                ? EnrichmentDraftSerializer.ToWebSnippets(draft.Snippets).ToList()
                : await SelectCurrentSnippetsAsync(topic, cacheRow, ct);
            if (snippets.Count == 0) return new ReviewActionOutcome(ReviewActionResult.Invalid, NoSnippetsMessage);

            var summarized = await medicationSummarizer.SummarizeAsync(job.SourceDisplayName, snippets, ct);
            if (!summarized.Success || summarized.Summary is null)
                return new ReviewActionOutcome(ReviewActionResult.UpstreamFailed, summarized.Error ?? "Модель не вернула ответ.");

            var resolution = MedicationNameCorrection.Resolve(job.NormalizedName, job.SourceDisplayName, summarized.Summary);
            var source = EnrichmentReviewGate.BuildSourceLabel(providerName, snippets, summarized.Summary.UsedSourceIndexes);
            job.DraftPayloadJson = EnrichmentDraftSerializer.Serialize(new MedicationDraft(
                resolution.NormalizedName, resolution.DisplayName, resolution.ExtraAliases, source, summarized.Summary,
                EnrichmentDraftSerializer.ToDraftSnippets(snippets), summarized.FieldSources));
            job.ResultConfidence = summarized.Confidence;
            job.ResultConfidenceReason = summarized.ConfidenceReason;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Пересуммаризация {Kind} {JobId} «{Name}»: уверенность {Confidence}.",
            kind, job.Id, job.SourceDisplayName, job.ResultConfidence);
        return ReviewActionOutcome.Ok;
    }

    // ------------------------------------------------------------------ карточка сущности (вне очереди)

    /// <summary>Карточка записи справочника вне очереди (ADR-0018, вариант C): запись, её статус проверки, набор источников
    /// кэша и задача в очереди, если она сейчас ждёт решения. kind: lab-analyte | medication.</summary>
    public async Task<ReviewEntityDto?> GetEntityAsync(string kind, Guid kbId, CancellationToken ct = default)
    {
        if (kind == ReviewKinds.LabAnalyte)
        {
            var kb = await catalog.GetLabAnalyteAsync(kbId, ct);
            if (kb is null) return null;
            var group = await labCache.GetSearchGroupAsync(kb.SpecimenKbId, ct);
            var row = await labCache.GetByNameAsync(kb.NormalizedName, kb.SpecimenKbId, ct);
            var sources = await BuildSourcesAsync(WebSearchTopic.LabAnalyte, row, new HashSet<string>(), ct);
            var queued = await db.LabAnalyteEnrichmentJobs.AsNoTracking()
                .Where(j => j.NormalizedName == kb.NormalizedName && j.SpecimenKbId == kb.SpecimenKbId
                    && EnrichmentJobStatusSets.AwaitingAdmin.Contains(j.Status))
                .Select(j => new { j.Id, j.Status }).FirstOrDefaultAsync(ct);
            return new ReviewEntityDto(
                kind, ToCurrent(kb), kb.SpecimenDisplayName, ToCacheDto(WebSearchTopic.LabAnalyte, row, group.GroupKey, group.QueryLabel),
                sources, queued?.Id, queued is null ? null : StageOf(queued.Status));
        }

        if (kind is ReviewKinds.Medication or ReviewKinds.VisitMedication)
        {
            var kb = await catalog.GetMedicationAsync(kbId, ct);
            if (kb is null) return null;
            var row = await medCache.GetByNameAsync(kb.NormalizedName, ct);
            var sources = await BuildSourcesAsync(WebSearchTopic.Medication, row, new HashSet<string>(), ct);
            var queuedMed = await db.MedicationEnrichmentJobs.AsNoTracking()
                .Where(j => j.NormalizedName == kb.NormalizedName && EnrichmentJobStatusSets.AwaitingAdmin.Contains(j.Status))
                .Select(j => new { j.Id, j.Status }).FirstOrDefaultAsync(ct);
            var queuedVisit = queuedMed is not null ? null : await db.VisitMedicationEnrichmentJobs.AsNoTracking()
                .Where(j => j.NormalizedName == kb.NormalizedName && EnrichmentJobStatusSets.AwaitingAdmin.Contains(j.Status))
                .Select(j => new { j.Id, j.Status }).FirstOrDefaultAsync(ct);
            var queued = queuedMed ?? queuedVisit;
            return new ReviewEntityDto(
                kind, ToCurrent(kb), null, ToCacheDto(WebSearchTopic.Medication, row, null, null), sources,
                queued?.Id, queued is null ? null : StageOf(queued.Status));
        }

        return null;
    }

    private static string StageOf(EnrichmentJobStatus status) =>
        status == EnrichmentJobStatus.AwaitingSearchApproval ? ReviewStages.Search : ReviewStages.Result;

    /// <summary>Предложение новой версии записи по текущему набору сниппетов кэша — без записи в kb и без платного поиска.
    /// Админ принимает его через редактор (правка с локами и журналом) либо отбрасывает.</summary>
    public async Task<ReviewActionOutcome<ReviewResummarizePreviewDto>> PreviewResummarizeAsync(
        string kind, Guid kbId, CancellationToken ct = default)
    {
        if (kind == ReviewKinds.LabAnalyte)
        {
            var kb = await catalog.GetLabAnalyteAsync(kbId, ct);
            if (kb is null) return new ReviewActionOutcome<ReviewResummarizePreviewDto>(ReviewActionResult.NotFound);
            var row = await labCache.GetByNameAsync(kb.NormalizedName, kb.SpecimenKbId, ct);
            if (row is null) return Fail("Для записи нет кэша источников — добавьте сниппеты или запустите платный поиск.");

            var snippets = await SelectCurrentSnippetsAsync(WebSearchTopic.LabAnalyte, row, ct);
            if (snippets.Count == 0) return Fail(NoSnippetsMessage);

            var summarized = await labSummarizer.SummarizeAsync(kb.DisplayName, snippets, ct);
            if (!summarized.Success || summarized.Summary is null)
                return new ReviewActionOutcome<ReviewResummarizePreviewDto>(ReviewActionResult.UpstreamFailed, null, summarized.Error ?? "Модель не вернула ответ.");

            var domains = await trustedDomains.GetActiveDomainsByPriorityAsync(WebSearchTopic.LabAnalyte, ct);
            var summary = summarized.Summary with { RefRanges = ReferenceRangeMerger.Merge(summarized.Summary.RefRanges, snippets, domains) };
            var draftSnippets = EnrichmentDraftSerializer.ToDraftSnippets(snippets);
            var payload = LabAnalyteKbPayload.Build(summary);
            var info = BuildFieldSourceInfo(LabCheckedFields, payload, summarized.FieldSources, draftSnippets, summary);
            return new ReviewActionOutcome<ReviewResummarizePreviewDto>(ReviewActionResult.Ok, new ReviewResummarizePreviewDto(
                LabAnalyteNameCleaner.Clean(kb.DisplayName), payload, LabAnalyteKbWriter.BuildAliases(kb.NormalizedName, summary).ToList(),
                summarized.Confidence, summarized.ConfidenceReason, info, UsedSources(draftSnippets, summary.UsedSourceIndexes)));
        }

        if (kind is ReviewKinds.Medication or ReviewKinds.VisitMedication)
        {
            var kb = await catalog.GetMedicationAsync(kbId, ct);
            if (kb is null) return new ReviewActionOutcome<ReviewResummarizePreviewDto>(ReviewActionResult.NotFound);
            var row = await medCache.GetByNameAsync(kb.NormalizedName, ct);
            if (row is null) return Fail("Для записи нет кэша источников — добавьте сниппеты или запустите платный поиск.");

            var snippets = await SelectCurrentSnippetsAsync(WebSearchTopic.Medication, row, ct);
            if (snippets.Count == 0) return Fail(NoSnippetsMessage);

            var summarized = await medicationSummarizer.SummarizeAsync(kb.DisplayName, snippets, ct);
            if (!summarized.Success || summarized.Summary is null)
                return new ReviewActionOutcome<ReviewResummarizePreviewDto>(ReviewActionResult.UpstreamFailed, null, summarized.Error ?? "Модель не вернула ответ.");

            var draftSnippets = EnrichmentDraftSerializer.ToDraftSnippets(snippets);
            var payload = KbWriter.BuildPayloadJson(summarized.Summary);
            var info = BuildFieldSourceInfo(MedicationCheckedFields, payload, summarized.FieldSources, draftSnippets, null);
            return new ReviewActionOutcome<ReviewResummarizePreviewDto>(ReviewActionResult.Ok, new ReviewResummarizePreviewDto(
                kb.DisplayName, payload, KbWriter.BuildAliases(kb.NormalizedName, summarized.Summary, null).ToList(),
                summarized.Confidence, summarized.ConfidenceReason, info, UsedSources(draftSnippets, summarized.Summary.UsedSourceIndexes)));
        }

        return new ReviewActionOutcome<ReviewResummarizePreviewDto>(ReviewActionResult.NotFound);

        static ReviewActionOutcome<ReviewResummarizePreviewDto> Fail(string message) =>
            new(ReviewActionResult.Invalid, null, message);
    }

    private static List<ReviewSourceDto> UsedSources(IReadOnlyList<DraftSnippet> snippets, IReadOnlyList<int> usedIndexes) =>
        usedIndexes.Where(i => i >= 0 && i < snippets.Count).Distinct().Select(i =>
        {
            var s = snippets[i];
            var expert = SnippetKinds.IsExpertUrl(s.Url);
            return new ReviewSourceDto(
                i, s.Title, s.Url, string.Empty, expert ? SnippetKinds.ExpertSourceLabel : HostOf(s.Url), false, true, null,
                s.Origin, s.Kind, null, false, true, false);
        }).ToList();

    // ------------------------------------------------------------------ пороги

    public async Task<EnrichmentReviewConfigDto> GetConfigAsync(CancellationToken ct = default)
    {
        var t = await config.GetAsync(ct);
        var updatedAt = await db.EnrichmentReviewConfigs.AsNoTracking().Select(c => (DateTime?)c.UpdatedAt).FirstOrDefaultAsync(ct);
        return new EnrichmentReviewConfigDto(
            t.MedicationQueryMinConfidence, t.AnalyteQueryMinConfidence,
            t.MedicationResultMinConfidence, t.AnalyteResultMinConfidence, updatedAt);
    }

    /// <summary>false — значение вне диапазона 0..1.</summary>
    public async Task<bool> SetConfigAsync(SetEnrichmentReviewConfigRequest request, CancellationToken ct = default)
    {
        try
        {
            await config.SetAsync(new EnrichmentReviewThresholds(
                request.MedicationQueryMinConfidence, request.AnalyteQueryMinConfidence,
                request.MedicationResultMinConfidence, request.AnalyteResultMinConfidence), null, ct);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ общее

    private async Task<IReviewableEnrichmentJob?> LoadAsync(string kind, Guid id, CancellationToken ct, bool tracking = true)
    {
        switch (kind)
        {
            case ReviewKinds.LabAnalyte:
                return await (tracking ? db.LabAnalyteEnrichmentJobs : db.LabAnalyteEnrichmentJobs.AsNoTracking())
                    .FirstOrDefaultAsync(j => j.Id == id, ct);
            case ReviewKinds.Medication:
                return await (tracking ? db.MedicationEnrichmentJobs : db.MedicationEnrichmentJobs.AsNoTracking())
                    .FirstOrDefaultAsync(j => j.Id == id, ct);
            case ReviewKinds.VisitMedication:
                return await (tracking ? db.VisitMedicationEnrichmentJobs : db.VisitMedicationEnrichmentJobs.AsNoTracking())
                    .FirstOrDefaultAsync(j => j.Id == id, ct);
            default:
                return null;
        }
    }

    private async Task<Guid?> ResolveMedkitIdAsync(Guid? medicationId, CancellationToken ct)
    {
        if (medicationId is null) return null;
        return await db.Set<Medication>().AsNoTracking()
            .Where(m => m.Id == medicationId.Value).Select(m => (Guid?)m.MedkitId).FirstOrDefaultAsync(ct);
    }
}
