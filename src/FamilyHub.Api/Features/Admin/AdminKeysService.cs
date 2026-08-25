using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Security;
using FamilyHub.Infrastructure.Security.Rotation;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Api.Features.Admin;

public enum StartRotationResult
{
    /// <summary>Прогон создан/возобновлён и поставлен в очередь Hangfire "rotation".</summary>
    Started,

    /// <summary>Связка не содержит отставных ключей — перешифровывать нечего (кнопка в UI
    /// должна быть неактивна в этом состоянии, это защита на случай прямого вызова API).</summary>
    NothingToRotate,
}

/// <summary>
/// Управление ротацией ключа шифрования из админ-панели (ADR-0009) — старт/резюме, отмена,
/// живой статус. Сама перешифровка выполняется в фоне (EncryptionRotationJob, очередь Hangfire
/// "rotation"), этот сервис только создаёт/читает/помечает строку EncryptionRotationRun.
/// </summary>
public class AdminKeysService(AppDbContext db, IEncryptionKeyRing keyRing, IBackgroundJobClient backgroundJobs)
{
    public async Task<StartRotationResult> StartOrResumeRotationAsync(CancellationToken ct = default)
    {
        var existing = await db.EncryptionRotationRuns
            .FirstOrDefaultAsync(r => r.Status == EncryptionRotationStatus.Running, ct);
        if (existing is null)
        {
            if (keyRing.PreviousKeyIds.Count == 0)
                return StartRotationResult.NothingToRotate;

            db.EncryptionRotationRuns.Add(new EncryptionRotationRun
            {
                Id = Guid.NewGuid(),
                TargetKeyId = keyRing.ActiveKeyId,
                Status = EncryptionRotationStatus.Running,
                StartedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
        // Уже идущий прогон — просто ещё раз ставим джобу в очередь (идемпотентно резюмируемая,
        // см. EncryptionRotationJob) на случай, если предыдущее исполнение оборвалось и ночной
        // добиватель ещё не подхватил — админ не обязан ждать до 04:00.

        backgroundJobs.Enqueue<EncryptionRotationJob>(job => job.RunAsync(CancellationToken.None));
        return StartRotationResult.Started;
    }

    /// <summary>true, если был активный прогон, которому выставлен CancelRequested (сама джоба
    /// останавливается на ближайшей проверке между страницами — см. EncryptionRotationJob).</summary>
    public async Task<bool> RequestCancelAsync(CancellationToken ct = default)
    {
        var affected = await db.EncryptionRotationRuns
            .Where(r => r.Status == EncryptionRotationStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CancelRequested, true), ct);
        return affected > 0;
    }

    public async Task<RotationStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        // Последний по StartedAt — покрывает и текущий Running, и только что завершившийся
        // (Completed/Cancelled/Failed), чтобы UI показал финальный результат сразу после
        // окончания поллинга, не "как будто прогона никогда не было".
        var run = await db.EncryptionRotationRuns.AsNoTracking()
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);
        if (run is null)
            return new RotationStatusDto(null, null, null, null, null, null, 0, 0, 0, 0);

        return new RotationStatusDto(
            run.Id, run.TargetKeyId, run.Status.ToString(), run.StartedAt, run.FinishedAt, run.LastError,
            run.FieldsProcessed, run.FieldsTotal, run.BlobsProcessed, run.BlobsTotal);
    }
}
