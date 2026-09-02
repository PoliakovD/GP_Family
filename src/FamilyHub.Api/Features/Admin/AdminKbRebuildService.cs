using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Extraction;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Api.Features.Admin;

public enum StartKbRebuildResult
{
    /// <summary>Прогон создан/возобновлён и поставлен в очередь Hangfire "enrichment".</summary>
    Started,

    /// <summary>Прогон уже идёт — повторный клик просто ещё раз поставил ту же задачу в очередь
    /// (резюмируется с сохранённого этапа, см. LabAnalyteKbRebuildJob), новый не создан.</summary>
    AlreadyRunning,
}

public record KbRebuildStatusDto(
    Guid? RunId, string? Status, DateTime? StartedAt, DateTime? FinishedAt, string? LastError, int StageIndex,
    int CacheMerged, int IndicatorsUpdated, int IndicatorsMerged, int CatalogDeleted, int ReseedRequested);

/// <summary>
/// Управление пересборкой справочника показателей из админ-панели (пересборка enrich-пайплайна,
/// §4.2 плана) — старт/резюме, живой статус. Сама пересборка выполняется в фоне
/// (LabAnalyteKbRebuildJob, очередь Hangfire "enrichment"), этот сервис только создаёт/читает
/// строку KbRebuildRun — зеркало AdminKeysService на другую джобу.
/// </summary>
public class AdminKbRebuildService(AppDbContext db, IBackgroundJobClient backgroundJobs)
{
    public async Task<StartKbRebuildResult> StartOrResumeAsync(CancellationToken ct = default)
    {
        var existing = await db.KbRebuildRuns.FirstOrDefaultAsync(r => r.Status == KbRebuildStatus.Running, ct);
        var isNew = existing is null;
        if (isNew)
        {
            existing = new KbRebuildRun { Id = Guid.NewGuid(), Status = KbRebuildStatus.Running, StartedAt = DateTime.UtcNow };
            db.KbRebuildRuns.Add(existing);
            await db.SaveChangesAsync(ct);
        }

        backgroundJobs.Enqueue<LabAnalyteKbRebuildJob>(job => job.RunAsync(existing!.Id, CancellationToken.None));
        return isNew ? StartKbRebuildResult.Started : StartKbRebuildResult.AlreadyRunning;
    }

    public async Task<KbRebuildStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        // Последний по StartedAt — покрывает и текущий Running, и только что завершившийся, чтобы
        // UI показал финальный результат сразу после окончания поллинга (тот же приём, что
        // AdminKeysService.GetStatusAsync).
        var run = await db.KbRebuildRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);
        if (run is null) return new KbRebuildStatusDto(null, null, null, null, null, 0, 0, 0, 0, 0, 0);

        return new KbRebuildStatusDto(
            run.Id, run.Status.ToString(), run.StartedAt, run.FinishedAt, run.LastError, run.StageIndex,
            run.CacheMerged, run.IndicatorsUpdated, run.IndicatorsMerged, run.CatalogDeleted, run.ReseedRequested);
    }
}
