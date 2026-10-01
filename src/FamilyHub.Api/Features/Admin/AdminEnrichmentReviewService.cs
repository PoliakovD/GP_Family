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
/// (поле зарезервировано) — аудит решений ведёт сам факт смены статуса/ReviewedAt и лог.
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
    IDomainEventPublisher publisher,
    IBackgroundJobClient backgroundJobs,
    ILogger<AdminEnrichmentReviewService> logger)
{
    /// <summary>Потолок строк на вид в выдаче очереди — очередь ручная, тысячи строк не ожидаются;
    /// сортировка «ниже порога первыми» делается в памяти поверх этой выборки.</summary>
    private const int MaxRowsPerKind = 500;

    private const int MaxQueryLength = 300;

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
        Guid Id, string Kind, string Name, string NormalizedName, string? Specimen, string Origin,
        string? ProposedQuery, double? QueryConfidence, string? QueryConfidenceReason,
        double? ResultConfidence, string? ResultConfidenceReason, string? Provider, DateTime CreatedAt);

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
                    j.Id, j.SourceDisplayName, j.NormalizedName, j.Origin, j.ProposedQueryText, j.QueryConfidence,
                    j.QueryConfidenceReason, j.ResultConfidence, j.ResultConfidenceReason, j.Provider, j.CreatedAt,
                    Specimen = db.GlobalSpecimensKb.Where(s => s.Id == j.SpecimenKbId).Select(s => s.DisplayName).FirstOrDefault(),
                })
                .ToListAsync(ct);
            rows.AddRange(lab.Select(j => new QueueRow(
                j.Id, ReviewKinds.LabAnalyte, j.SourceDisplayName, j.NormalizedName, j.Specimen, LabOriginCode(j.Origin),
                j.ProposedQueryText, j.QueryConfidence, j.QueryConfidenceReason, j.ResultConfidence, j.ResultConfidenceReason,
                j.Provider, j.CreatedAt)));
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
                })
                .ToListAsync(ct);
            rows.AddRange(med.Select(j => new QueueRow(
                j.Id, ReviewKinds.Medication, j.SourceDisplayName, j.NormalizedName, null, "medkit",
                j.ProposedQueryText, j.QueryConfidence, j.QueryConfidenceReason, j.ResultConfidence, j.ResultConfidenceReason,
                j.Provider, j.CreatedAt)));
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
                })
                .ToListAsync(ct);
            rows.AddRange(visit.Select(j => new QueueRow(
                j.Id, ReviewKinds.VisitMedication, j.SourceDisplayName, j.NormalizedName, null, "visit",
                j.ProposedQueryText, j.QueryConfidence, j.QueryConfidenceReason, j.ResultConfidence, j.ResultConfidenceReason,
                j.Provider, j.CreatedAt)));
        }

        return rows;
    }

    private static string LabOriginCode(EnrichmentRequestOrigin origin) => origin switch
    {
        EnrichmentRequestOrigin.ManualEntry => "manual",
        EnrichmentRequestOrigin.SystemMaintenance => "maintenance",
        _ => "extraction",
    };

    /// <summary>Сортировка очереди: «ниже порога» (в том числе без оценки) первыми, внутри — старые
    /// раньше (FIFO), чтобы ничто не залёживалось.</summary>
    public async Task<ReviewSearchListResponse> ListSearchesAsync(string? kind, int skip, int take, CancellationToken ct = default)
    {
        var thresholds = await config.GetAsync(ct);
        var rows = await LoadRowsAsync(kind, EnrichmentJobStatus.AwaitingSearchApproval, ct);

        var items = rows.Select(r =>
        {
            var threshold = thresholds.QueryMin(DomainOf(r.Kind));
            return new ReviewSearchItemDto(
                r.Id, r.Kind, r.Name, r.Specimen, string.IsNullOrWhiteSpace(r.ProposedQuery) ? r.NormalizedName : r.ProposedQuery,
                r.QueryConfidence, r.QueryConfidenceReason, threshold,
                !EnrichmentReviewThresholds.IsConfident(r.QueryConfidence, threshold), r.Origin, r.CreatedAt);
        })
            .OrderByDescending(i => i.BelowThreshold).ThenBy(i => i.CreatedAt)
            .ToList();

        return new ReviewSearchListResponse(items.Skip(skip).Take(take).ToList(), items.Count);
    }

    public async Task<ReviewResultListResponse> ListResultsAsync(string? kind, int skip, int take, CancellationToken ct = default)
    {
        var thresholds = await config.GetAsync(ct);
        var rows = await LoadRowsAsync(kind, EnrichmentJobStatus.AwaitingResultReview, ct);

        var items = rows.Select(r =>
        {
            var threshold = thresholds.ResultMin(DomainOf(r.Kind));
            return new ReviewResultItemDto(
                r.Id, r.Kind, r.Name, r.Specimen, r.ResultConfidence, r.ResultConfidenceReason, threshold,
                !EnrichmentReviewThresholds.IsConfident(r.ResultConfidence, threshold), r.Provider, r.CreatedAt);
        })
            .OrderByDescending(i => i.BelowThreshold).ThenBy(i => i.CreatedAt)
            .ToList();

        return new ReviewResultListResponse(items.Skip(skip).Take(take).ToList(), items.Count);
    }

    private static EnrichmentReviewDomain DomainOf(string kind) =>
        kind == ReviewKinds.LabAnalyte ? EnrichmentReviewDomain.Analyte : EnrichmentReviewDomain.Medication;

    // ------------------------------------------------------------------ детали результата

    public async Task<ReviewResultDetailDto?> GetResultAsync(string kind, Guid id, CancellationToken ct = default)
    {
        var job = await LoadAsync(kind, id, ct, tracking: false);
        if (job is null || job.Status != EnrichmentJobStatus.AwaitingResultReview || string.IsNullOrEmpty(job.DraftPayloadJson))
            return null;

        var thresholds = await config.GetAsync(ct);
        var threshold = thresholds.ResultMin(DomainOf(kind));
        var belowThreshold = !EnrichmentReviewThresholds.IsConfident(job.ResultConfidence, threshold);

        ReviewDraftDto draftDto;
        ReviewCurrentKbDto? current;
        IReadOnlyList<DraftSnippet> snippets;
        IReadOnlyList<int> used;
        string? specimen = null;

        if (kind == ReviewKinds.LabAnalyte)
        {
            var draft = EnrichmentDraftSerializer.Deserialize<LabAnalyteDraft>(job.DraftPayloadJson)!;
            draftDto = new ReviewDraftDto(
                draft.NormalizedName, LabAnalyteNameCleaner.Clean(draft.DisplayName), LabAnalyteKbPayload.Build(draft.Summary),
                LabAnalyteKbWriter.BuildAliases(draft.NormalizedName, draft.Summary).ToList(), draft.Source);
            var existing = await catalog.GetLabAnalyteByKeyAsync(draft.NormalizedName, draft.SpecimenKbId, ct);
            current = existing is null ? null : new ReviewCurrentKbDto(
                existing.Id, existing.DisplayName, existing.PayloadJson, existing.Aliases.ToList(), existing.LockedFields.ToList(), existing.UpdatedAt);
            snippets = draft.Snippets;
            used = draft.Summary.UsedSourceIndexes;
            specimen = await db.GlobalSpecimensKb.AsNoTracking()
                .Where(s => s.Id == draft.SpecimenKbId).Select(s => s.DisplayName).FirstOrDefaultAsync(ct);
        }
        else
        {
            var draft = EnrichmentDraftSerializer.Deserialize<MedicationDraft>(job.DraftPayloadJson)!;
            draftDto = new ReviewDraftDto(
                draft.NormalizedName, draft.DisplayName, KbWriter.BuildPayloadJson(draft.Summary),
                KbWriter.BuildAliases(draft.NormalizedName, draft.Summary, draft.ExtraAliases).ToList(), draft.Source);
            var existing = await catalog.GetMedicationByNormalizedNameAsync(draft.NormalizedName, ct);
            current = existing is null ? null : new ReviewCurrentKbDto(
                existing.Id, existing.DisplayName, existing.PayloadJson, existing.Aliases.ToList(), existing.LockedFields.ToList(), existing.UpdatedAt);
            snippets = draft.Snippets;
            used = draft.Summary.UsedSourceIndexes;
        }

        var snippetDtos = snippets
            .Select((s, i) => new ReviewSnippetDto(i, s.Title, s.Url, s.Text, used.Contains(i)))
            .ToList();

        return new ReviewResultDetailDto(
            job.Id, kind, job.SourceDisplayName, specimen, job.ResultConfidence, job.ResultConfidenceReason, threshold,
            belowThreshold, job.QueryConfidence, job.QueryConfidenceReason, job.Provider, job.CreatedAt,
            draftDto, current, snippetDtos);
    }

    // ------------------------------------------------------------------ гейт 1: поиски

    public async Task<ReviewActionOutcome> ApproveSearchAsync(string kind, Guid id, string? queryText, CancellationToken ct = default)
    {
        var job = await LoadAsync(kind, id, ct);
        if (job is null) return new ReviewActionOutcome(ReviewActionResult.NotFound);
        if (job.Status != EnrichmentJobStatus.AwaitingSearchApproval)
            return new ReviewActionOutcome(ReviewActionResult.WrongStatus, "Задача уже обработана.");

        string? query = null;
        if (queryText is not null)
        {
            query = queryText.Trim();
            if (query.Length == 0) return new ReviewActionOutcome(ReviewActionResult.Invalid, "Текст запроса не может быть пустым.");
            if (query.Length > MaxQueryLength)
                return new ReviewActionOutcome(ReviewActionResult.Invalid, $"Текст запроса длиннее {MaxQueryLength} символов.");
            // Правка админа уходит наружу в платный провайдер (ADR-0005 — egress только названий):
            // тот же гейт на персональный контекст, что у записи в справочник.
            var violation = KbIsolationGuard.FindViolation([query]);
            if (violation is not null)
                return new ReviewActionOutcome(ReviewActionResult.Invalid, $"Подозрение на персональный контекст: {violation}");
        }

        if (query is not null) job.ProposedQueryText = query;
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

        logger.LogInformation("Одобрен платный поиск {Kind} {JobId} «{Name}», запрос «{Query}».",
            kind, job.Id, job.SourceDisplayName, EnrichmentReviewGate.EffectiveQuery(job));
        return ReviewActionOutcome.Ok;
    }

    public async Task<ReviewActionOutcome> RejectSearchAsync(string kind, Guid id, string? reason, CancellationToken ct = default) =>
        await RejectAsync(kind, id, EnrichmentJobStatus.AwaitingSearchApproval, reason, "Платный поиск отклонён администратором.", ct);

    public async Task<BulkReviewResponse> BulkApproveSearchesAsync(IEnumerable<ReviewItemRef> items, CancellationToken ct = default)
    {
        var failed = new List<ReviewItemRef>();
        var processed = 0;
        foreach (var item in items.DistinctBy(i => (i.Kind, i.Id)))
        {
            var outcome = ReviewKinds.IsValid(item.Kind)
                ? await ApproveSearchAsync(item.Kind, item.Id, null, ct)
                : new ReviewActionOutcome(ReviewActionResult.Invalid);
            if (outcome.Result == ReviewActionResult.Ok) processed++;
            else failed.Add(item);
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
                ? await RejectSearchAsync(item.Kind, item.Id, reason, ct)
                : new ReviewActionOutcome(ReviewActionResult.Invalid);
            if (outcome.Result == ReviewActionResult.Ok) processed++;
            else failed.Add(item);
        }
        return new BulkReviewResponse(processed, failed);
    }

    // ------------------------------------------------------------------ гейт 2: результаты

    public async Task<ReviewActionOutcome> RejectResultAsync(string kind, Guid id, string? reason, CancellationToken ct = default) =>
        await RejectAsync(kind, id, EnrichmentJobStatus.AwaitingResultReview, reason, "Результат отклонён администратором.", ct);

    private async Task<ReviewActionOutcome> RejectAsync(
        string kind, Guid id, EnrichmentJobStatus expected, string? reason, string defaultMessage, CancellationToken ct)
    {
        var job = await LoadAsync(kind, id, ct);
        if (job is null) return new ReviewActionOutcome(ReviewActionResult.NotFound);
        if (job.Status != expected) return new ReviewActionOutcome(ReviewActionResult.WrongStatus, "Задача уже обработана.");

        // Failed + RejectedByAdmin — смысловой отказ человека. Пользователя не уведомляем (это не
        // сбой системы), повторный автозапрос того же имени блокирует обычная проверка alreadyFailed
        // в *RequestService, пока админ сам не перезапустит.
        job.Status = EnrichmentJobStatus.Failed;
        job.FailureReason = EnrichmentFailureReason.RejectedByAdmin;
        job.Error = string.IsNullOrWhiteSpace(reason) ? defaultMessage : reason.Trim();
        job.IsTransientFailure = false;
        job.CompletedAt = DateTime.UtcNow;
        job.ReviewedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Отклонено {Kind} {JobId} «{Name}» ({Stage}): {Reason}",
            kind, job.Id, job.SourceDisplayName, expected, job.Error);
        return ReviewActionOutcome.Ok;
    }

    /// <summary>Одобрение результата (с правками или без): запись в kb существующими писателями,
    /// затем — если админ что-то поправил — тем же путём, что ручная правка каталога
    /// (<see cref="AdminCatalogService"/>), поэтому правленые поля попадают в LockedFields. Всё в
    /// одной транзакции вместе со сменой статуса и публикацией события.</summary>
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

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        Guid kbId;
        string draftPayload;
        string draftName;
        string[] draftAliases;
        Func<string, string> normalizeAlias;
        string finalDisplayName;
        Guid? medkitId = null;

        if (kind == ReviewKinds.LabAnalyte)
        {
            var draft = EnrichmentDraftSerializer.Deserialize<LabAnalyteDraft>(job.DraftPayloadJson)!;
            var write = await labWriter.UpsertAsync(
                draft.NormalizedName, draft.SpecimenKbId, draft.DisplayName, draft.Summary, draft.Source, ct);
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
        }
        else
        {
            var draft = EnrichmentDraftSerializer.Deserialize<MedicationDraft>(job.DraftPayloadJson)!;
            var write = await kbWriter.UpsertAsync(
                draft.NormalizedName, draft.DisplayName, draft.Summary, draft.Source, draft.ExtraAliases, ct);
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
        }

        var edit = BuildEdit(request, draftPayload, draftName, draftAliases, normalizeAlias);
        if (edit is not null)
        {
            var (result, reason) = kind == ReviewKinds.LabAnalyte
                ? await UpdateLabAnalyteAsync(kbId, edit, ct)
                : await UpdateMedicationAsync(kbId, edit, ct);
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

        job.Status = EnrichmentJobStatus.Completed;
        job.KbId = kbId;
        job.Error = null;
        job.FailureReason = null;
        job.CompletedAt = DateTime.UtcNow;
        job.ReviewedAt = DateTime.UtcNow;

        if (kind == ReviewKinds.Medication && job is MedicationEnrichmentJob medicationJob)
        {
            medkitId = await ResolveMedkitIdAsync(medicationJob.MedicationId, ct);
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

    private async Task<(AdminKbEditResult Result, string? Reason)> UpdateLabAnalyteAsync(
        Guid kbId, AdminKbEditRequest edit, CancellationToken ct)
    {
        var (result, _, reason) = await catalog.UpdateLabAnalyteAsync(kbId, edit, ct);
        return (result, reason);
    }

    private async Task<(AdminKbEditResult Result, string? Reason)> UpdateMedicationAsync(
        Guid kbId, AdminKbEditRequest edit, CancellationToken ct)
    {
        var (result, _, reason) = await catalog.UpdateMedicationAsync(kbId, edit, ct);
        return (result, reason);
    }

    /// <summary>Что админ реально поменял относительно черновика. Не изменённое поле остаётся null
    /// (AdminCatalogService его не трогает и не лочит); для payload лочатся только отличающиеся
    /// ключи верхнего уровня ("payload.&lt;key&gt;"), а не весь payload — как режим формы редактора.</summary>
    internal static AdminKbEditRequest? BuildEdit(
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

    /// <summary>Пересуммаризация «из тех же сниппетов» — без нового платного поиска. Новый черновик
    /// заменяет старый, задача остаётся на ревью (даже при высокой уверенности: решение за админом).</summary>
    public async Task<ReviewActionOutcome> ResummarizeAsync(string kind, Guid id, CancellationToken ct = default)
    {
        var job = await LoadAsync(kind, id, ct);
        if (job is null) return new ReviewActionOutcome(ReviewActionResult.NotFound);
        if (job.Status != EnrichmentJobStatus.AwaitingResultReview || string.IsNullOrEmpty(job.DraftPayloadJson))
            return new ReviewActionOutcome(ReviewActionResult.WrongStatus, "Задача не ждёт ревью результата.");

        var providerName = job.Provider ?? "web";
        if (kind == ReviewKinds.LabAnalyte)
        {
            var draft = EnrichmentDraftSerializer.Deserialize<LabAnalyteDraft>(job.DraftPayloadJson)!;
            var snippets = EnrichmentDraftSerializer.ToWebSnippets(draft.Snippets);
            var summarized = await labSummarizer.SummarizeAsync(job.SourceDisplayName, snippets, ct);
            if (!summarized.Success || summarized.Summary is null)
                return new ReviewActionOutcome(ReviewActionResult.UpstreamFailed, summarized.Error ?? "Модель не вернула ответ.");

            var domains = await trustedDomains.GetActiveDomainsByPriorityAsync(WebSearchTopic.LabAnalyte, ct);
            var merged = ReferenceRangeMerger.Merge(summarized.Summary.RefRanges, snippets, domains);
            var summary = summarized.Summary with { RefRanges = merged };
            var source = EnrichmentReviewGate.BuildSourceLabel(providerName, snippets, summary.UsedSourceIndexes);
            job.DraftPayloadJson = EnrichmentDraftSerializer.Serialize(
                new LabAnalyteDraft(draft.NormalizedName, draft.SpecimenKbId, draft.DisplayName, source, summary, draft.Snippets));
            job.ResultConfidence = summarized.Confidence;
            job.ResultConfidenceReason = summarized.ConfidenceReason;
        }
        else
        {
            var draft = EnrichmentDraftSerializer.Deserialize<MedicationDraft>(job.DraftPayloadJson)!;
            var snippets = EnrichmentDraftSerializer.ToWebSnippets(draft.Snippets);
            var summarized = await medicationSummarizer.SummarizeAsync(job.SourceDisplayName, snippets, ct);
            if (!summarized.Success || summarized.Summary is null)
                return new ReviewActionOutcome(ReviewActionResult.UpstreamFailed, summarized.Error ?? "Модель не вернула ответ.");

            var resolution = MedicationNameCorrection.Resolve(job.NormalizedName, job.SourceDisplayName, summarized.Summary);
            var source = EnrichmentReviewGate.BuildSourceLabel(providerName, snippets, summarized.Summary.UsedSourceIndexes);
            job.DraftPayloadJson = EnrichmentDraftSerializer.Serialize(new MedicationDraft(
                resolution.NormalizedName, resolution.DisplayName, resolution.ExtraAliases, source, summarized.Summary, draft.Snippets));
            job.ResultConfidence = summarized.Confidence;
            job.ResultConfidenceReason = summarized.ConfidenceReason;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Пересуммаризация {Kind} {JobId} «{Name}»: уверенность {Confidence}.",
            kind, job.Id, job.SourceDisplayName, job.ResultConfidence);
        return ReviewActionOutcome.Ok;
    }

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
