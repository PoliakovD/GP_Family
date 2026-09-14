using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Prompts;
using FamilyHub.Modules.Medical.Enrichment;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.Pipeline;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FamilyHub.Api.Features.Admin;

/// <summary>
/// Управление enrich-пайплайном из админки (§2 плана): вкл/выкл необязательных шагов,
/// версионирование промптов (создание/откат — ничего не удаляется, откат = активация старой
/// версии тем же способом, что и создание новой), dry-run промпта без записи в справочник, и
/// листинг задач всех четырёх конвейеров (LabAnalyteEnrichmentJob/MedicationEnrichmentJob/
/// VisitMedicationEnrichmentJob/MedicalDocumentExtractionJob) — раньше видны были только через
/// сырой Hangfire-дашборд.
///
/// Реордер шагов (был в исходном плане) сюда сознательно не входит — реальная
/// последовательность вызовов зашита в процессорах (жёсткие зависимости между шагами одного
/// прогона: OCR-коррекция обязана случиться ДО поиска в справочнике, справочник — ДО расчёта
/// персонального референса), безопасный реордер потребовал бы переписать процессоры в
/// полноценный step-runner — вне объёма этой итерации (см. class doc PipelineCatalog).
/// </summary>
public static class AdminPipelineEndpoints
{
    private const string AttentionCacheKey = "admin:pipeline:attention";
    private static readonly TimeSpan AttentionCacheTtl = TimeSpan.FromSeconds(60);

    public static void MapAdminPipelineEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/pipeline").RequireAuthorization("PlatformAdmin");

        // Инбокс «Требует внимания» — точка входа админки в этот раздел (см. §3/§7 плана): что
        // сломано и почему, сгруппировано, вместо ручного разбора списка задач построчно.
        // Разбор кэша поиска по каждой Failed-задаче не бесплатен — короткий TTL, тот же приём,
        // что AdminEndpoints.GetStorageStatsAsync (там 15 минут — там дороже и меняется реже).
        group.MapGet("/attention", async (AdminAttentionService attention, IMemoryCache cache, CancellationToken ct) =>
        {
            if (cache.TryGetValue(AttentionCacheKey, out AdminAttentionDto? cached) && cached is not null)
                return Results.Ok(cached);

            var fresh = await attention.GetAttentionAsync(ct);
            cache.Set(AttentionCacheKey, fresh, AttentionCacheTtl);
            return Results.Ok(fresh);
        });

        // «Доверить все и перезапустить N» — батч-действие на самый частый отброшенный домен(ы)
        // прямо из инбокса, без перехода в «Обогащение» и без открытия каждой задачи по отдельности.
        group.MapPost("/attention/trust-and-retry", async (
            TrustAndRetryRequest request, AppDbContext db, EnrichmentTrustedDomainService trustedDomains,
            IBackgroundJobClient backgroundJobs, IMemoryCache cache, CancellationToken ct) =>
        {
            foreach (var domain in request.Domains)
                await trustedDomains.AddAsync(request.Topic, domain, ct);

            var retried = 0;
            if (request.Topic == WebSearchTopic.LabAnalyte)
            {
                var ids = await db.LabAnalyteEnrichmentJobs.AsNoTracking()
                    .Where(j => j.Status == EnrichmentJobStatus.Failed && j.FailureReason == EnrichmentFailureReason.NoTrustedSnippets)
                    .Select(j => j.Id).Take(100).ToListAsync(ct);
                foreach (var id in ids)
                    if (await ResetAndEnqueueAsync("lab-analyte", id, db, backgroundJobs, ct)) retried++;
            }
            else
            {
                var medIds = await db.MedicationEnrichmentJobs.AsNoTracking()
                    .Where(j => j.Status == EnrichmentJobStatus.Failed && j.FailureReason == EnrichmentFailureReason.NoTrustedSnippets)
                    .Select(j => j.Id).Take(100).ToListAsync(ct);
                foreach (var id in medIds)
                    if (await ResetAndEnqueueAsync("medication", id, db, backgroundJobs, ct)) retried++;

                var visitIds = await db.VisitMedicationEnrichmentJobs.AsNoTracking()
                    .Where(j => j.Status == EnrichmentJobStatus.Failed && j.FailureReason == EnrichmentFailureReason.NoTrustedSnippets)
                    .Select(j => j.Id).Take(100 - retried).ToListAsync(ct);
                foreach (var id in visitIds)
                    if (await ResetAndEnqueueAsync("visit-medication", id, db, backgroundJobs, ct)) retried++;
            }

            cache.Remove(AttentionCacheKey);
            return Results.Ok(new TrustAndRetryResponse(retried));
        });

        group.MapGet("/pipelines", async (AppDbContext db, CancellationToken ct) =>
        {
            var configs = await db.PipelineStepConfigs.AsNoTracking()
                .ToDictionaryAsync(s => (s.PipelineKey, s.StepKey), s => s.IsEnabled, ct);

            var steps = PipelineCatalog.Steps.Select(s => new PipelineStepDto(
                s.PipelineKey, s.StepKey, s.Description, s.IsMandatory,
                s.IsMandatory || !configs.TryGetValue((s.PipelineKey, s.StepKey), out var enabled) || enabled,
                s.PromptKey)).ToList();

            return Results.Ok(steps);
        });

        group.MapPut("/pipelines/{pipelineKey}/steps/{stepKey}", async (
            string pipelineKey, string stepKey, SetStepEnabledRequest request,
            AppDbContext db, IPipelineConfigService pipelineConfig, CancellationToken ct) =>
        {
            var declaration = PipelineCatalog.Find(pipelineKey, stepKey);
            if (declaration is null) return Results.NotFound();
            if (declaration.IsMandatory)
                return Results.Json(
                    new { code = "mandatory_step", message = "Обязательный шаг нельзя выключить." },
                    statusCode: StatusCodes.Status409Conflict);

            var config = await db.PipelineStepConfigs
                .FirstOrDefaultAsync(s => s.PipelineKey == pipelineKey && s.StepKey == stepKey, ct);
            if (config is null)
            {
                config = new Domain.Entities.PipelineStepConfig
                {
                    Id = Guid.NewGuid(), PipelineKey = pipelineKey, StepKey = stepKey,
                };
                db.PipelineStepConfigs.Add(config);
            }
            config.IsEnabled = request.IsEnabled;
            config.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            pipelineConfig.Invalidate(pipelineKey, stepKey);
            return Results.NoContent();
        });

        group.MapGet("/prompts", async (AppDbContext db, CancellationToken ct) =>
        {
            var active = await db.PipelinePromptVersions.AsNoTracking()
                .Where(v => v.IsActive)
                .Select(v => new { v.Prompt.Key, v.Version, v.CreatedAt })
                .ToDictionaryAsync(v => v.Key, v => (v.Version, v.CreatedAt), ct);

            var slots = PromptCatalog.Prompts.Select(p =>
            {
                active.TryGetValue(p.Key, out var a);
                return new PromptSlotDto(p.Key, p.Description, a.Version == 0 ? null : a.Version, a.Version == 0 ? null : a.CreatedAt);
            }).ToList();

            return Results.Ok(slots);
        });

        group.MapGet("/prompts/{key}/versions", async (string key, AppDbContext db, CancellationToken ct) =>
        {
            var versions = await db.PipelinePromptVersions.AsNoTracking()
                .Where(v => v.Prompt.Key == key)
                .OrderByDescending(v => v.Version)
                .Select(v => new PromptVersionDto(v.Id, v.Version, v.IsActive, v.Note, v.CreatedAt, v.Body))
                .ToListAsync(ct);

            return versions.Count == 0 ? Results.NotFound() : Results.Ok(versions);
        });

        // Создаёт И СРАЗУ активирует новую версию — откат делается тем же способом (активация
        // старой версии, POST .../activate/{version} ниже), явного "черновика" не заводим:
        // проще прогнать dry-run перед сохранением (см. ниже), чем поддерживать состояние
        // "версия существует, но неактивна и не была активна".
        group.MapPost("/prompts/{key}/versions", async (
            string key, CreatePromptVersionRequest request,
            AppDbContext db, IPromptProvider promptProvider, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Body)) return Results.BadRequest(new { message = "Текст промпта не может быть пустым." });

            var prompt = await db.PipelinePrompts.FirstOrDefaultAsync(p => p.Key == key, ct);
            if (prompt is null) return Results.NotFound();

            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var maxVersion = await db.PipelinePromptVersions
                .Where(v => v.PromptId == prompt.Id)
                .Select(v => (int?)v.Version)
                .MaxAsync(ct) ?? 0;

            await db.PipelinePromptVersions.Where(v => v.PromptId == prompt.Id && v.IsActive)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.IsActive, false), ct);

            var version = new Domain.Entities.PipelinePromptVersion
            {
                Id = Guid.NewGuid(),
                PromptId = prompt.Id,
                Version = maxVersion + 1,
                Body = request.Body,
                IsActive = true,
                Note = request.Note,
                CreatedAt = DateTime.UtcNow,
            };
            db.PipelinePromptVersions.Add(version);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            promptProvider.Invalidate(key);
            return Results.Created(
                $"/api/admin/pipeline/prompts/{key}/versions",
                new PromptVersionDto(version.Id, version.Version, true, version.Note, version.CreatedAt, version.Body));
        });

        // Откат — активация уже существующей (в т.ч. старой) версии, ничего не удаляется.
        group.MapPost("/prompts/{key}/activate/{version:int}", async (
            string key, int version, AppDbContext db, IPromptProvider promptProvider, CancellationToken ct) =>
        {
            var prompt = await db.PipelinePrompts.FirstOrDefaultAsync(p => p.Key == key, ct);
            if (prompt is null) return Results.NotFound();

            var target = await db.PipelinePromptVersions
                .FirstOrDefaultAsync(v => v.PromptId == prompt.Id && v.Version == version, ct);
            if (target is null) return Results.NotFound();

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.PipelinePromptVersions.Where(v => v.PromptId == prompt.Id && v.IsActive)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.IsActive, false), ct);
            target.IsActive = true;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            promptProvider.Invalidate(key);
            return Results.NoContent();
        });

        // Прогоняет промпт (активную версию слота, если BodyOverride не задан — для проверки
        // черновика ДО сохранения) по свободному тексту без всякой доменной логики и без записи —
        // ноль внешнего трафика: тест самого промпта, не всего конвейера.
        group.MapPost("/prompts/dry-run", async (
            DryRunRequest request, ILmStudioJsonClient client, IPromptProvider promptProvider, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.UserText))
                return Results.BadRequest(new { message = "Нужен пример текста для прогона." });

            string body;
            if (!string.IsNullOrWhiteSpace(request.BodyOverride))
            {
                body = request.BodyOverride;
            }
            else
            {
                var declaration = PromptCatalog.Prompts.FirstOrDefault(p => p.Key == request.PromptKey);
                if (declaration is null) return Results.NotFound(new { message = "Неизвестный ключ промпта." });
                // Фолбэка на константу кода тут нет намеренно — dry-run без BodyOverride имеет
                // смысл только когда в БД уже есть активная версия, которую хотят проверить.
                body = await promptProvider.GetAsync(request.PromptKey, string.Empty, ct);
                if (body.Length == 0)
                    return Results.BadRequest(new { message = "У этого промпта нет ни активной версии в БД, ни BodyOverride." });
            }

            var result = await client.ExtractJsonAsync(body, request.UserText, ct);
            return Results.Ok(new DryRunResponse(result.Success, result.Error, result.Payload));
        });

        group.MapGet("/jobs", async (
            string? type, string? status, string? reason, int? skip, int? take, AppDbContext db, CancellationToken ct) =>
        {
            var take2 = Math.Clamp(take ?? 25, 1, 100);
            var skip2 = Math.Max(skip ?? 0, 0);
            EnrichmentJobStatus? statusFilter = Enum.TryParse<EnrichmentJobStatus>(status, true, out var s) ? s : null;

            // "Unclassified" — синтетический фильтр (см. AdminAttentionService), не член
            // EnrichmentFailureReason: задачи, упавшие ДО появления структурной причины, у них
            // FailureReason=NULL. Отличаем его от "фильтр не задан" (reason пуст), иначе клик
            // «Открыть задачи» из карточки Unclassified в «Требует внимания» молча показал бы ВСЕ
            // Failed-задачи типа, а не только те самые "без объяснений".
            var hasReasonFilter = !string.IsNullOrEmpty(reason);
            var reasonIsUnclassified = string.Equals(reason, "Unclassified", StringComparison.OrdinalIgnoreCase);
            EnrichmentFailureReason? reasonFilter = hasReasonFilter && !reasonIsUnclassified
                && Enum.TryParse<EnrichmentFailureReason>(reason, true, out var r) ? r : null;

            var response = type switch
            {
                "lab-analyte" => await ListAsync(db.LabAnalyteEnrichmentJobs.AsNoTracking()
                    .Where(j => statusFilter == null || j.Status == statusFilter)
                    .Where(j => !hasReasonFilter || (reasonIsUnclassified ? j.FailureReason == null : j.FailureReason == reasonFilter))
                    .OrderByDescending(j => j.CreatedAt),
                    j => new PipelineJobDto(j.Id, "lab-analyte", j.SourceDisplayName, j.Status.ToString(), j.Attempts, j.Error, j.CreatedAt, j.StartedAt, j.CompletedAt, j.FailureReason == null ? null : j.FailureReason.ToString()),
                    skip2, take2, ct),
                "medication" => await ListAsync(db.MedicationEnrichmentJobs.AsNoTracking()
                    .Where(j => statusFilter == null || j.Status == statusFilter)
                    .Where(j => !hasReasonFilter || (reasonIsUnclassified ? j.FailureReason == null : j.FailureReason == reasonFilter))
                    .OrderByDescending(j => j.CreatedAt),
                    j => new PipelineJobDto(j.Id, "medication", j.SourceDisplayName, j.Status.ToString(), j.Attempts, j.Error, j.CreatedAt, j.StartedAt, j.CompletedAt, j.FailureReason == null ? null : j.FailureReason.ToString()),
                    skip2, take2, ct),
                "visit-medication" => await ListAsync(db.VisitMedicationEnrichmentJobs.AsNoTracking()
                    .Where(j => statusFilter == null || j.Status == statusFilter)
                    .Where(j => !hasReasonFilter || (reasonIsUnclassified ? j.FailureReason == null : j.FailureReason == reasonFilter))
                    .OrderByDescending(j => j.CreatedAt),
                    j => new PipelineJobDto(j.Id, "visit-medication", j.SourceDisplayName, j.Status.ToString(), j.Attempts, j.Error, j.CreatedAt, j.StartedAt, j.CompletedAt, j.FailureReason == null ? null : j.FailureReason.ToString()),
                    skip2, take2, ct),
                "extraction" => await ListAsync(db.MedicalDocumentExtractionJobs.AsNoTracking()
                    .Where(j => statusFilter == null || j.Status == statusFilter)
                    .Where(j => !hasReasonFilter || (reasonIsUnclassified ? j.FailureReason == null : j.FailureReason == reasonFilter))
                    .OrderByDescending(j => j.CreatedAt),
                    j => new PipelineJobDto(j.Id, "extraction", j.MedicalRecordId.ToString(), j.Status.ToString(), j.Attempts, j.Error, j.CreatedAt, j.StartedAt, j.CompletedAt, j.FailureReason == null ? null : j.FailureReason.ToString()),
                    skip2, take2, ct),
                _ => (PipelineJobListResponse?)null,
            };

            return response is null
                ? Results.BadRequest(new { message = "type обязателен: lab-analyte|medication|visit-medication|extraction." })
                : Results.Ok(response);
        });

        // Карточка одной задачи для боковой панели («Требует внимания» → карточка, список задач →
        // карточка — тот же компонент на фронте) — раньше причину отказа и связанные сниппеты
        // приходилось искать вручную на двух разных вкладках (см. план, Context).
        group.MapGet("/jobs/{id:guid}", async (
            Guid id, string type, AppDbContext db, LabAnalyteSearchCacheService labCache,
            MedicationSearchCacheService medCache, EnrichmentTrustedDomainService trustedDomains, CancellationToken ct) =>
        {
            switch (type)
            {
                case "lab-analyte":
                {
                    var job = await db.LabAnalyteEnrichmentJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
                    if (job is null) return Results.NotFound();

                    var specimenName = await db.GlobalSpecimensKb.AsNoTracking()
                        .Where(sp => sp.Id == job.SpecimenKbId).Select(sp => sp.DisplayName).FirstOrDefaultAsync(ct);
                    var allDomains = await trustedDomains.GetAllAsync(WebSearchTopic.LabAnalyte, ct);
                    var activeDomains = allDomains.Where(d => d.IsEnabled).OrderBy(d => d.Rank).Select(d => d.Domain).ToList();

                    var cacheRow = await labCache.GetByNameAsync(job.NormalizedName, job.SpecimenKbId, ct);
                    SearchCacheDetailDto? searchCache = null;
                    if (cacheRow is not null)
                    {
                        var cached = await labCache.GetCachedAsync(job.NormalizedName, job.SpecimenKbId, ct);
                        searchCache = AdminEnrichmentEndpoints.BuildDetail(
                            cacheRow.Id, cacheRow.NormalizedName, specimenName, cacheRow.Provider,
                            cacheRow.LastUpdatedAt, cacheRow.CanBeUpdatedAfter,
                            cached?.Snippets ?? [], cached?.Overrides, activeDomains);
                    }

                    return Results.Ok(new PipelineJobDetailDto(
                        job.Id, "lab-analyte", job.SourceDisplayName, job.Status.ToString(), job.Attempts, job.Error,
                        job.FailureReason?.ToString(), job.CreatedAt, job.StartedAt, job.CompletedAt,
                        job.NormalizedName, job.SpecimenKbId, specimenName, job.Origin.ToString(), job.Force,
                        job.Provider, job.ExternalSearchAt, job.IsTransientFailure, job.KbId,
                        searchCache, allDomains.Select(d => new TrustedDomainDto(d.Id, d.Domain, d.Rank, d.IsEnabled)).ToList()));
                }
                case "medication":
                {
                    var job = await db.MedicationEnrichmentJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
                    if (job is null) return Results.NotFound();

                    var allDomains = await trustedDomains.GetAllAsync(WebSearchTopic.Medication, ct);
                    var activeDomains = allDomains.Where(d => d.IsEnabled).OrderBy(d => d.Rank).Select(d => d.Domain).ToList();

                    var cacheRow = await medCache.GetByNameAsync(job.NormalizedName, ct);
                    SearchCacheDetailDto? searchCache = null;
                    if (cacheRow is not null)
                    {
                        var cached = await medCache.GetCachedAsync(job.NormalizedName, ct);
                        searchCache = AdminEnrichmentEndpoints.BuildDetail(
                            cacheRow.Id, cacheRow.NormalizedName, null, cacheRow.Provider,
                            cacheRow.LastUpdatedAt, cacheRow.CanBeUpdatedAfter,
                            cached?.Snippets ?? [], cached?.Overrides, activeDomains);
                    }

                    return Results.Ok(new PipelineJobDetailDto(
                        job.Id, "medication", job.SourceDisplayName, job.Status.ToString(), job.Attempts, job.Error,
                        job.FailureReason?.ToString(), job.CreatedAt, job.StartedAt, job.CompletedAt,
                        job.NormalizedName, null, null, null, false,
                        job.Provider, job.ExternalSearchAt, job.IsTransientFailure, job.KbId,
                        searchCache, allDomains.Select(d => new TrustedDomainDto(d.Id, d.Domain, d.Rank, d.IsEnabled)).ToList()));
                }
                case "visit-medication":
                {
                    var job = await db.VisitMedicationEnrichmentJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
                    if (job is null) return Results.NotFound();

                    var allDomains = await trustedDomains.GetAllAsync(WebSearchTopic.Medication, ct);
                    var activeDomains = allDomains.Where(d => d.IsEnabled).OrderBy(d => d.Rank).Select(d => d.Domain).ToList();

                    var cacheRow = await medCache.GetByNameAsync(job.NormalizedName, ct);
                    SearchCacheDetailDto? searchCache = null;
                    if (cacheRow is not null)
                    {
                        var cached = await medCache.GetCachedAsync(job.NormalizedName, ct);
                        searchCache = AdminEnrichmentEndpoints.BuildDetail(
                            cacheRow.Id, cacheRow.NormalizedName, null, cacheRow.Provider,
                            cacheRow.LastUpdatedAt, cacheRow.CanBeUpdatedAfter,
                            cached?.Snippets ?? [], cached?.Overrides, activeDomains);
                    }

                    // VisitMedicationEnrichmentJob не несёт IsTransientFailure (см. class doc —
                    // зеркало MedicationEnrichmentJob без семейного контура) — терминальный технический
                    // сбой здесь неотличим от смыслового кроме как по FailureReason.
                    return Results.Ok(new PipelineJobDetailDto(
                        job.Id, "visit-medication", job.SourceDisplayName, job.Status.ToString(), job.Attempts, job.Error,
                        job.FailureReason?.ToString(), job.CreatedAt, job.StartedAt, job.CompletedAt,
                        job.NormalizedName, null, null, null, false,
                        job.Provider, job.ExternalSearchAt, false, job.KbId,
                        searchCache, allDomains.Select(d => new TrustedDomainDto(d.Id, d.Domain, d.Rank, d.IsEnabled)).ToList()));
                }
                case "extraction":
                {
                    var job = await db.MedicalDocumentExtractionJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
                    if (job is null) return Results.NotFound();

                    return Results.Ok(new PipelineJobDetailDto(
                        job.Id, "extraction", job.MedicalRecordId.ToString(), job.Status.ToString(), job.Attempts, job.Error,
                        job.FailureReason?.ToString(), job.CreatedAt, job.StartedAt, job.CompletedAt,
                        null, null, null, null, false, null, null, job.IsTransientFailure, null, null, []));
                }
                default:
                    return Results.BadRequest(new { message = "type обязателен: lab-analyte|medication|visit-medication|extraction." });
            }
        });

        group.MapPost("/jobs/{id:guid}/retry", async (
            Guid id, string type, AppDbContext db, IBackgroundJobClient backgroundJobs, IMemoryCache cache, CancellationToken ct) =>
        {
            if (type is not ("lab-analyte" or "medication" or "visit-medication" or "extraction"))
                return Results.BadRequest(new { message = "type обязателен: lab-analyte|medication|visit-medication|extraction." });

            var reset = await ResetAndEnqueueAsync(type, id, db, backgroundJobs, ct);
            if (reset) cache.Remove(AttentionCacheKey);
            return reset ? Results.Accepted() : Results.NotFound();
        });

        // «Применить и перезапустить» из карточки задачи — доверяет домены/override'ы конкретных
        // URL и перезапускает ОДНИМ запросом, вместо перехода на вкладку «Обогащение» и обратно
        // (см. план, Context). У extraction нет ни доменов, ни сниппетов — не поддерживается.
        group.MapPost("/jobs/{id:guid}/resolve-and-retry", async (
            Guid id, string type, ResolveAndRetryRequest request, AppDbContext db,
            LabAnalyteSearchCacheService labCache, MedicationSearchCacheService medCache,
            EnrichmentTrustedDomainService trustedDomains, IBackgroundJobClient backgroundJobs,
            IMemoryCache cache, CancellationToken ct) =>
        {
            string normalizedName;
            Guid? specimenKbId;
            WebSearchTopic topic;

            switch (type)
            {
                case "lab-analyte":
                {
                    var job = await db.LabAnalyteEnrichmentJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
                    if (job is null) return Results.NotFound();
                    normalizedName = job.NormalizedName;
                    specimenKbId = job.SpecimenKbId;
                    topic = WebSearchTopic.LabAnalyte;
                    break;
                }
                case "medication":
                {
                    var job = await db.MedicationEnrichmentJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
                    if (job is null) return Results.NotFound();
                    normalizedName = job.NormalizedName;
                    specimenKbId = null;
                    topic = WebSearchTopic.Medication;
                    break;
                }
                case "visit-medication":
                {
                    var job = await db.VisitMedicationEnrichmentJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
                    if (job is null) return Results.NotFound();
                    normalizedName = job.NormalizedName;
                    specimenKbId = null;
                    topic = WebSearchTopic.Medication;
                    break;
                }
                default:
                    return Results.BadRequest(new { message = "type должен быть lab-analyte|medication|visit-medication — у extraction нет сниппетов/доменов." });
            }

            // 1. Домены доверия — раньше override'ов: сам факт добавления домена уже включает
            // все его сниппеты (EnrichmentSnippetFilter), дубликат — success:false, не ошибка запроса.
            foreach (var domain in request.TrustDomains ?? [])
                await trustedDomains.AddAsync(topic, domain, ct);

            // 2. Override конкретных URL — строка кэша существует, только если по этому имени уже
            // был хотя бы один платный поиск (иначе фронт не смог бы прислать Overrides вовсе).
            if (request.Overrides is { Count: > 0 })
            {
                var cacheId = topic == WebSearchTopic.LabAnalyte
                    ? (await labCache.GetByNameAsync(normalizedName, specimenKbId!.Value, ct))?.Id
                    : (await medCache.GetByNameAsync(normalizedName, ct))?.Id;

                if (cacheId is not null)
                {
                    foreach (var item in request.Overrides)
                    {
                        if (topic == WebSearchTopic.LabAnalyte)
                            await labCache.SetSnippetOverrideAsync(cacheId.Value, item.Url, item.Enabled, ct);
                        else
                            await medCache.SetSnippetOverrideAsync(cacheId.Value, item.Url, item.Enabled, ct);
                    }
                }
            }

            // 3. Сброс задачи и перезапуск — после того, как БД уже видит новые домены/override'ы
            // (единственный воркер очереди enrichment не должен подхватить задачу раньше коммита).
            var reset = await ResetAndEnqueueAsync(type, id, db, backgroundJobs, ct);
            if (reset) cache.Remove(AttentionCacheKey);
            return reset ? Results.Accepted() : Results.NotFound();
        });

        // Массовый перезапуск — чекбоксы в списке задач («Требует внимания» → «Доверить все и
        // перезапустить N», список задач → «Перезапустить N выбранных»).
        group.MapPost("/jobs/bulk-retry", async (
            BulkRetryRequest request, AppDbContext db, IBackgroundJobClient backgroundJobs, IMemoryCache cache, CancellationToken ct) =>
        {
            if (request.Type is not ("lab-analyte" or "medication" or "visit-medication" or "extraction"))
                return Results.BadRequest(new { message = "type обязателен: lab-analyte|medication|visit-medication|extraction." });
            if (request.Ids.Count == 0)
                return Results.BadRequest(new { message = "ids не может быть пустым." });
            if (request.Ids.Count > 100)
                return Results.BadRequest(new { message = "не более 100 задач за один запрос." });

            var notFound = new List<Guid>();
            var retried = 0;
            foreach (var id in request.Ids.Distinct())
            {
                if (await ResetAndEnqueueAsync(request.Type, id, db, backgroundJobs, ct)) retried++;
                else notFound.Add(id);
            }
            if (retried > 0) cache.Remove(AttentionCacheKey);
            return Results.Ok(new BulkRetryResponse(retried, notFound));
        });

        // Удаление одной задачи — не перезапуск, строка убирается насовсем (карточка задачи,
        // список задач). Безопасно для любого статуса: процессоры уже устойчивы к "задача не
        // найдена" (см. каждый RunAsync — не найдена, значит уже обработана/удалена, тихий выход).
        group.MapDelete("/jobs/{id:guid}", async (
            Guid id, string type, AppDbContext db, IMemoryCache cache, CancellationToken ct) =>
        {
            if (type is not ("lab-analyte" or "medication" or "visit-medication" or "extraction"))
                return Results.BadRequest(new { message = "type обязателен: lab-analyte|medication|visit-medication|extraction." });

            var deleted = await DeleteJobAsync(type, id, db, ct);
            if (deleted) cache.Remove(AttentionCacheKey);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        // Массовое удаление — чекбоксы в списке задач, тот же принцип, что bulk-retry.
        group.MapPost("/jobs/bulk-delete", async (
            BulkDeleteRequest request, AppDbContext db, IMemoryCache cache, CancellationToken ct) =>
        {
            if (request.Type is not ("lab-analyte" or "medication" or "visit-medication" or "extraction"))
                return Results.BadRequest(new { message = "type обязателен: lab-analyte|medication|visit-medication|extraction." });
            if (request.Ids.Count == 0)
                return Results.BadRequest(new { message = "ids не может быть пустым." });
            if (request.Ids.Count > 100)
                return Results.BadRequest(new { message = "не более 100 задач за один запрос." });

            var notFound = new List<Guid>();
            var deleted = 0;
            foreach (var id in request.Ids.Distinct())
            {
                if (await DeleteJobAsync(request.Type, id, db, ct)) deleted++;
                else notFound.Add(id);
            }
            if (deleted > 0) cache.Remove(AttentionCacheKey);
            return Results.Ok(new BulkDeleteResponse(deleted, notFound));
        });

        // Чистка задач, упавших ДО этой правки (FailureReason ещё не проставлялся — см.
        // EnrichmentFailureReason) — «Требует внимания» показывает их отдельным пунктом
        // "Unclassified", разбирать их по одной незачем: причины у них по определению нет,
        // структурно они больше ничего не расскажут. Один запрос чистит все четыре конвейера сразу.
        group.MapPost("/jobs/purge-unclassified", async (AppDbContext db, IMemoryCache cache, CancellationToken ct) =>
        {
            var lab = await db.LabAnalyteEnrichmentJobs
                .Where(j => j.Status == EnrichmentJobStatus.Failed && j.FailureReason == null)
                .ExecuteDeleteAsync(ct);
            var med = await db.MedicationEnrichmentJobs
                .Where(j => j.Status == EnrichmentJobStatus.Failed && j.FailureReason == null)
                .ExecuteDeleteAsync(ct);
            var visit = await db.VisitMedicationEnrichmentJobs
                .Where(j => j.Status == EnrichmentJobStatus.Failed && j.FailureReason == null)
                .ExecuteDeleteAsync(ct);
            var extraction = await db.MedicalDocumentExtractionJobs
                .Where(j => j.Status == EnrichmentJobStatus.Failed && j.FailureReason == null)
                .ExecuteDeleteAsync(ct);

            cache.Remove(AttentionCacheKey);
            return Results.Ok(new PurgeUnclassifiedResponse(lab, med, visit, extraction, lab + med + visit + extraction));
        });

        // Точечное принудительное переобогащение одной уже существующей строки справочника
        // показателей (см. LabAnalyteEnrichmentJob.Force) — не батч, как /api/admin/kb/lab-analytes/reenrich.
        group.MapPost("/kb/lab-analytes/{id:guid}/reenrich", async (
            Guid id, AppDbContext db, IBackgroundJobClient backgroundJobs, CancellationToken ct) =>
        {
            var kb = await db.GlobalLabAnalytesKb.AsNoTracking().FirstOrDefaultAsync(k => k.Id == id, ct);
            if (kb is null) return Results.NotFound();

            var job = new Domain.Entities.LabAnalyteEnrichmentJob
            {
                Id = Guid.NewGuid(),
                NormalizedName = kb.NormalizedName,
                SpecimenKbId = kb.SpecimenKbId,
                SourceDisplayName = kb.DisplayName,
                Force = true,
                RequestedByUserId = Guid.Empty,
                Status = EnrichmentJobStatus.Pending,
                CreatedAt = DateTime.UtcNow,
            };
            db.LabAnalyteEnrichmentJobs.Add(job);
            await db.SaveChangesAsync(ct);
            backgroundJobs.Enqueue<LabAnalyteEnrichmentProcessor>(p => p.RunAsync(job.Id, CancellationToken.None));
            return Results.Accepted();
        });

        // Одноразовый перепрогон (план "нормы из бланка: односторонние референсы и качественные
        // результаты") — чинит показатели, застрявшие на Flag.Unknown ДО фикса каскада
        // IndicatorFlagCalculator (RecomputeIndicatorFlagsBackfillJob), не часть обычного конвейера.
        group.MapPost("/recompute-indicator-flags", (IBackgroundJobClient backgroundJobs) =>
        {
            backgroundJobs.Enqueue<RecomputeIndicatorFlagsBackfillJob>(j => j.RunAsync(CancellationToken.None));
            return Results.Accepted();
        });
    }

    /// <summary>Сброс задачи в Pending (снимает Error/FailureReason — задача перезапускается
    /// начисто) + постановка в очередь тем же процессором. Общий хвост для одиночного retry,
    /// resolve-and-retry и bulk-retry — раньше было четыре независимые копии этого switch.</summary>
    private static async Task<bool> ResetAndEnqueueAsync(
        string type, Guid id, AppDbContext db, IBackgroundJobClient backgroundJobs, CancellationToken ct)
    {
        switch (type)
        {
            case "lab-analyte":
            {
                var job = await db.LabAnalyteEnrichmentJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
                if (job is null) return false;
                job.Status = EnrichmentJobStatus.Pending;
                job.Error = null;
                job.FailureReason = null;
                await db.SaveChangesAsync(ct);
                backgroundJobs.Enqueue<LabAnalyteEnrichmentProcessor>(p => p.RunAsync(job.Id, CancellationToken.None));
                return true;
            }
            case "medication":
            {
                var job = await db.MedicationEnrichmentJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
                if (job is null) return false;
                job.Status = EnrichmentJobStatus.Pending;
                job.Error = null;
                job.FailureReason = null;
                await db.SaveChangesAsync(ct);
                backgroundJobs.Enqueue<MedicationEnrichmentProcessor>(p => p.RunAsync(job.Id, CancellationToken.None));
                return true;
            }
            case "visit-medication":
            {
                var job = await db.VisitMedicationEnrichmentJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
                if (job is null) return false;
                job.Status = EnrichmentJobStatus.Pending;
                job.Error = null;
                job.FailureReason = null;
                await db.SaveChangesAsync(ct);
                backgroundJobs.Enqueue<VisitMedicationEnrichmentProcessor>(p => p.RunAsync(job.Id, CancellationToken.None));
                return true;
            }
            case "extraction":
            {
                var job = await db.MedicalDocumentExtractionJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
                if (job is null) return false;
                job.Status = EnrichmentJobStatus.Pending;
                job.Error = null;
                job.FailureReason = null;
                await db.SaveChangesAsync(ct);
                backgroundJobs.Enqueue<MedicalDocumentExtractionProcessor>(p => p.RunAsync(job.Id, CancellationToken.None));
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>Удаляет одну задачу по (type, id) — общий хвост для одиночного и массового
    /// удаления, тот же приём, что ResetAndEnqueueAsync для retry.</summary>
    private static async Task<bool> DeleteJobAsync(string type, Guid id, AppDbContext db, CancellationToken ct)
    {
        var affected = type switch
        {
            "lab-analyte" => await db.LabAnalyteEnrichmentJobs.Where(j => j.Id == id).ExecuteDeleteAsync(ct),
            "medication" => await db.MedicationEnrichmentJobs.Where(j => j.Id == id).ExecuteDeleteAsync(ct),
            "visit-medication" => await db.VisitMedicationEnrichmentJobs.Where(j => j.Id == id).ExecuteDeleteAsync(ct),
            "extraction" => await db.MedicalDocumentExtractionJobs.Where(j => j.Id == id).ExecuteDeleteAsync(ct),
            _ => 0,
        };
        return affected > 0;
    }

    private static async Task<PipelineJobListResponse> ListAsync<TEntity>(
        IQueryable<TEntity> query, System.Linq.Expressions.Expression<Func<TEntity, PipelineJobDto>> project,
        int skip, int take, CancellationToken ct)
    {
        var total = await query.CountAsync(ct);
        var rows = await query.Skip(skip).Take(take).Select(project).ToListAsync(ct);
        return new PipelineJobListResponse(rows, total);
    }
}
