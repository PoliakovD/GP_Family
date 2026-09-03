using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Prompts;
using FamilyHub.Modules.Medical.Enrichment;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.Pipeline;
using Hangfire;
using Microsoft.EntityFrameworkCore;

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
    public static void MapAdminPipelineEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/pipeline").RequireAuthorization("PlatformAdmin");

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
            string? type, string? status, int? skip, int? take, AppDbContext db, CancellationToken ct) =>
        {
            var take2 = Math.Clamp(take ?? 25, 1, 100);
            var skip2 = Math.Max(skip ?? 0, 0);
            EnrichmentJobStatus? statusFilter = Enum.TryParse<EnrichmentJobStatus>(status, true, out var s) ? s : null;

            var response = type switch
            {
                "lab-analyte" => await ListAsync(db.LabAnalyteEnrichmentJobs.AsNoTracking()
                    .Where(j => statusFilter == null || j.Status == statusFilter)
                    .OrderByDescending(j => j.CreatedAt),
                    j => new PipelineJobDto(j.Id, "lab-analyte", j.SourceDisplayName, j.Status.ToString(), j.Attempts, j.Error, j.CreatedAt, j.StartedAt, j.CompletedAt),
                    skip2, take2, ct),
                "medication" => await ListAsync(db.MedicationEnrichmentJobs.AsNoTracking()
                    .Where(j => statusFilter == null || j.Status == statusFilter)
                    .OrderByDescending(j => j.CreatedAt),
                    j => new PipelineJobDto(j.Id, "medication", j.SourceDisplayName, j.Status.ToString(), j.Attempts, j.Error, j.CreatedAt, j.StartedAt, j.CompletedAt),
                    skip2, take2, ct),
                "visit-medication" => await ListAsync(db.VisitMedicationEnrichmentJobs.AsNoTracking()
                    .Where(j => statusFilter == null || j.Status == statusFilter)
                    .OrderByDescending(j => j.CreatedAt),
                    j => new PipelineJobDto(j.Id, "visit-medication", j.SourceDisplayName, j.Status.ToString(), j.Attempts, j.Error, j.CreatedAt, j.StartedAt, j.CompletedAt),
                    skip2, take2, ct),
                "extraction" => await ListAsync(db.MedicalDocumentExtractionJobs.AsNoTracking()
                    .Where(j => statusFilter == null || j.Status == statusFilter)
                    .OrderByDescending(j => j.CreatedAt),
                    j => new PipelineJobDto(j.Id, "extraction", j.MedicalRecordId.ToString(), j.Status.ToString(), j.Attempts, j.Error, j.CreatedAt, j.StartedAt, j.CompletedAt),
                    skip2, take2, ct),
                _ => (PipelineJobListResponse?)null,
            };

            return response is null
                ? Results.BadRequest(new { message = "type обязателен: lab-analyte|medication|visit-medication|extraction." })
                : Results.Ok(response);
        });

        group.MapPost("/jobs/{id:guid}/retry", async (
            Guid id, string type, AppDbContext db, IBackgroundJobClient backgroundJobs, CancellationToken ct) =>
        {
            switch (type)
            {
                case "lab-analyte":
                {
                    var job = await db.LabAnalyteEnrichmentJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
                    if (job is null) return Results.NotFound();
                    job.Status = EnrichmentJobStatus.Pending;
                    job.Error = null;
                    await db.SaveChangesAsync(ct);
                    backgroundJobs.Enqueue<LabAnalyteEnrichmentProcessor>(p => p.RunAsync(job.Id, CancellationToken.None));
                    break;
                }
                case "medication":
                {
                    var job = await db.MedicationEnrichmentJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
                    if (job is null) return Results.NotFound();
                    job.Status = EnrichmentJobStatus.Pending;
                    job.Error = null;
                    await db.SaveChangesAsync(ct);
                    backgroundJobs.Enqueue<MedicationEnrichmentProcessor>(p => p.RunAsync(job.Id, CancellationToken.None));
                    break;
                }
                case "visit-medication":
                {
                    var job = await db.VisitMedicationEnrichmentJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
                    if (job is null) return Results.NotFound();
                    job.Status = EnrichmentJobStatus.Pending;
                    job.Error = null;
                    await db.SaveChangesAsync(ct);
                    backgroundJobs.Enqueue<VisitMedicationEnrichmentProcessor>(p => p.RunAsync(job.Id, CancellationToken.None));
                    break;
                }
                case "extraction":
                {
                    var job = await db.MedicalDocumentExtractionJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
                    if (job is null) return Results.NotFound();
                    job.Status = EnrichmentJobStatus.Pending;
                    job.Error = null;
                    await db.SaveChangesAsync(ct);
                    backgroundJobs.Enqueue<MedicalDocumentExtractionProcessor>(p => p.RunAsync(job.Id, CancellationToken.None));
                    break;
                }
                default:
                    return Results.BadRequest(new { message = "type обязателен: lab-analyte|medication|visit-medication|extraction." });
            }

            return Results.Accepted();
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
