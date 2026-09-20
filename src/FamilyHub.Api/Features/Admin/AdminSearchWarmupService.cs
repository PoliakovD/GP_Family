using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Enrichment;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Api.Features.Admin;

public enum StartWarmupResult
{
    /// <summary>Прогон создан и поставлен в очередь Hangfire "enrichment".</summary>
    Started,

    /// <summary>Уже есть активный (Running/Paused) прогон — новый не создан, второй клик видит
    /// статус уже идущего.</summary>
    AlreadyRunning,

    /// <summary>После разбора/дедупа/фильтрации по длине строки список пуст — прогонять нечего.</summary>
    NothingToDo,

    /// <summary>Topic=LabAnalyte без валидного SpecimenKbId (или с сентинелом "не определено") —
    /// без него прогрев записал бы кэш на нерезолвленный источник (см. class doc
    /// LabAnalyteSearchCacheService.PurgeUnresolvedSpecimenAsync — ровно тот мусор, который она чистит).</summary>
    SpecimenRequired,
}

/// <summary>
/// Управление прогревом кэша веб-поиска из админки — старт/отмена/статус. Зеркало
/// AdminKbRebuildService на другую джобу (SearchCacheWarmupJob, см. её class doc), но с бюджетом
/// платных вызовов и явной отменой, которых у пересборки нет.
/// </summary>
public class AdminSearchWarmupService(AppDbContext db, IBackgroundJobClient backgroundJobs)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<(StartWarmupResult Result, WarmupStatusDto? Status)> StartAsync(
        StartWarmupRequest request, CancellationToken ct = default)
    {
        if (request.Topic == WebSearchTopic.LabAnalyte &&
            (request.SpecimenKbId is null || request.SpecimenKbId == SpecimenContextIds.Unresolved))
            return (StartWarmupResult.SpecimenRequired, null);

        var alreadyRunning = await db.SearchWarmupRuns
            .AnyAsync(r => r.Status == SearchWarmupStatus.Running || r.Status == SearchWarmupStatus.Paused, ct);
        if (alreadyRunning) return (StartWarmupResult.AlreadyRunning, await GetStatusAsync(ct));

        var names = WarmupNameParser.Parse(request.Names, request.Topic);
        if (names.Count == 0) return (StartWarmupResult.NothingToDo, null);

        var run = new SearchWarmupRun
        {
            Id = Guid.NewGuid(),
            Topic = request.Topic,
            SpecimenKbId = request.Topic == WebSearchTopic.LabAnalyte ? request.SpecimenKbId : null,
            NamesJson = JsonSerializer.Serialize(names, JsonOptions),
            TotalNames = names.Count,
            MaxPaidCalls = request.MaxPaidCalls,
            Status = SearchWarmupStatus.Running,
            StartedAt = DateTime.UtcNow,
        };
        db.SearchWarmupRuns.Add(run);

        // Та же гарантия, что и у request-сервисов конвейера (EnrichmentRequestService и т.п.):
        // Pending-строка и Hangfire-энкью — единая единица отката, сбой энкью не должен оставить
        // строку прогона без реальной задачи (она бы тогда молча блокировала повторный старт
        // уникальным индексом навсегда).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
            backgroundJobs.Enqueue<SearchCacheWarmupJob>(j => j.RunAsync(run.Id, CancellationToken.None));
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Гонка на уникальном индексе Status=Running — параллельный клик успел раньше.
            await tx.RollbackAsync(ct);
            return (StartWarmupResult.AlreadyRunning, await GetStatusAsync(ct));
        }
        catch (Exception)
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return (StartWarmupResult.Started, await GetStatusAsync(ct));
    }

    public async Task<bool> CancelAsync(CancellationToken ct = default)
    {
        var run = await db.SearchWarmupRuns
            .FirstOrDefaultAsync(r => r.Status == SearchWarmupStatus.Running || r.Status == SearchWarmupStatus.Paused, ct);
        if (run is null) return false;

        run.CancelRequested = true;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<WarmupStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        // Последний по StartedAt — покрывает и активный, и только что завершившийся прогон, чтобы
        // UI показал финальный результат сразу после остановки поллинга (тот же приём, что
        // AdminKbRebuildService.GetStatusAsync).
        var run = await db.SearchWarmupRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);
        if (run is null)
            return new WarmupStatusDto(null, null, null, null, 0, 0, 0, 0, 0, 0, null, null, null, null);

        string? specimenDisplayName = null;
        if (run.SpecimenKbId is { } specimenId)
        {
            specimenDisplayName = await db.GlobalSpecimensKb.AsNoTracking()
                .Where(s => s.Id == specimenId).Select(s => s.DisplayName).FirstOrDefaultAsync(ct);
        }

        return new WarmupStatusDto(
            run.Id, run.Status.ToString(), run.Topic, specimenDisplayName, run.TotalNames, run.Cursor,
            run.PaidCalls, run.SkippedKbHit, run.SkippedFreshCache, run.Failures, run.MaxPaidCalls,
            run.StartedAt, run.FinishedAt, run.LastError);
    }
}
