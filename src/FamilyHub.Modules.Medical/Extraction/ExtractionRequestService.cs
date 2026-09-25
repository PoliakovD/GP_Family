using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Persistence;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary><see cref="QueuedWaitingForAi"/> — задача СОЗДАНА, но ИИ (LM Studio) недоступен прямо сейчас:
/// она встаёт в очередь с флагом WaitingForAi и не уходит в Hangfire, пока LmStudioRecoverySweepJob
/// не увидит, что сервер снова отвечает. Пользователь ничего не теряет и не должен нажимать
/// «Распознать» повторно — фронт показывает «ждём ИИ».
/// <see cref="TooManyActiveJobs"/>/<see cref="DailyQuotaExceeded"/> — лимиты на пользователя
/// (ExtractionLimitsOptions), см. class doc ниже.</summary>
public enum ExtractionRequestResult
{
    Success, NotFound, Forbidden, AlreadyQueued, NothingToDo, QueuedWaitingForAi,
    TooManyActiveJobs, DailyQuotaExceeded,
}

/// <summary>
/// Постановка ЗАПИСИ в очередь распознавания (ветка medicalrecords, редизайн v2 — раньше был
/// per-attachment: за один клик обрабатывается ОДНО вложение, теперь одна кнопка «Распознать»
/// обрабатывает все ещё не распознанные вложения записи последовательно, см.
/// MedicalDocumentExtractionProcessor). По образцу EnrichmentRequestService.EnqueueAsync (этап 4):
/// Pending-строка и Hangfire-энкью в одной явной транзакции, дедуп через частичный уникальный
/// индекс по MedicalRecordId + catch DbUpdateException. Вызывается только владельцем записи (см.
/// ExtractionEndpoints) — тот же барьер, что и для загрузки/шаринга вложений.
/// </summary>
public class ExtractionRequestService(
    AppDbContext db,
    IBackgroundJobClient backgroundJobs,
    ILmStudioAvailabilityProbe probe,
    IOptions<ExtractionLimitsOptions> limits,
    ILogger<ExtractionRequestService> logger)
{
    public async Task<ExtractionRequestResult> RequestAsync(
        Guid recordId, Guid requestedByUserId, CancellationToken ct = default)
    {
        var record = await db.MedicalRecords.AsNoTracking()
            .Where(r => r.Id == recordId)
            .Select(r => new { r.OwnerUserId })
            .FirstOrDefaultAsync(ct);
        if (record is null) return ExtractionRequestResult.NotFound;
        if (record.OwnerUserId != requestedByUserId) return ExtractionRequestResult.Forbidden;

        // Явная предпроверка живой задачи — вместо того, чтобы узнавать о дубле только постфактум
        // по DbUpdateException на уникальном индексе ниже (та ловушка остаётся как подстраховка от
        // гонки, см. catch). Даёт вернуть понятный AlreadyQueued без лишнего похода в транзакцию.
        var hasLiveJob = await db.MedicalDocumentExtractionJobs.AsNoTracking()
            .AnyAsync(j => j.MedicalRecordId == recordId &&
                (j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running), ct);
        if (hasLiveJob) return ExtractionRequestResult.AlreadyQueued;

        // Лимиты на пользователя (ExtractionLimitsOptions, батч-загрузка) — МЯГКИЕ (две
        // параллельные постановки могут обе пройти эту проверку до вставки своей Pending-строки):
        // точный барьер потребовал бы блокировки, которой проект принципиально избегает (см.
        // patterns/backend.md, «Merge / race-условия» — уникальные индексы + перечитывание, не
        // явные локи). Частоту повторных попыток ограничивает rate limiting отдельно (Program.cs,
        // политика "llm"), этих двух вместе достаточно.
        var activeJobsCount = await db.MedicalDocumentExtractionJobs.AsNoTracking()
            .CountAsync(j => j.RequestedByUserId == requestedByUserId &&
                (j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running), ct);
        if (activeJobsCount >= limits.Value.MaxActiveJobsPerUser) return ExtractionRequestResult.TooManyActiveJobs;

        var todayStartUtc = DateTime.UtcNow.Date;
        var jobsToday = await db.MedicalDocumentExtractionJobs.AsNoTracking()
            .CountAsync(j => j.RequestedByUserId == requestedByUserId && j.CreatedAt >= todayStartUtc, ct);
        if (jobsToday >= limits.Value.DailyJobsPerUser) return ExtractionRequestResult.DailyQuotaExceeded;

        var hasPendingAttachments = await db.FileAttachments.AsNoTracking()
            .AnyAsync(a => a.OwnerType == FileOwnerType.MedicalRecord && a.OwnerId == recordId && a.ExtractedAt == null, ct);
        if (!hasPendingAttachments) return ExtractionRequestResult.NothingToDo;

        // ИИ недоступен прямо сейчас — задачу всё равно создаём (пользователь ничего не должен
        // терять и перенажимать), но в Hangfire не отдаём: она ждёт с WaitingForAi, а
        // LmStudioRecoverySweepJob запустит её, когда сервер снова ответит.
        var aiAvailable = await probe.IsAvailableAsync(ct);

        var job = new MedicalDocumentExtractionJob
        {
            Id = Guid.NewGuid(),
            MedicalRecordId = recordId,
            RequestedByUserId = requestedByUserId,
            Status = EnrichmentJobStatus.Pending,
            Stage = ExtractionStage.Queued,
            CreatedAt = DateTime.UtcNow,
            WaitingForAi = !aiAvailable,
        };
        db.MedicalDocumentExtractionJobs.Add(job);

        // Та же причина явной транзакции, что в EnrichmentRequestService.EnqueueAsync: Hangfire
        // использует отдельное соединение, сбой энкью не должен оставлять "висячую" Pending-строку,
        // которая навсегда заблокирует дедупом повторные попытки для этой записи.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Отражаем "задача в очереди" на самой записи (не только в таблице job) — без этого
            // пользователь, ушедший со страницы или обновивший её, не видит, что распознавание ещё
            // идёт (UI молчит до следующего ручного клика «Распознать», см. MedicalRecordsPanelComponent
            // .refresh/resumeLivePolling на фронте, которые как раз читают это поле).
            await db.MedicalRecords.Where(r => r.Id == recordId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ExtractionStatus, ExtractionStatus.Pending), ct);
            await db.SaveChangesAsync(ct);
            if (aiAvailable)
                backgroundJobs.Enqueue<MedicalDocumentExtractionProcessor>(p => p.RunAsync(job.Id, CancellationToken.None));
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            await tx.RollbackAsync(ct);
            logger.LogDebug(ex, "Распознавание мед-записи {RecordId} уже в очереди, пропускаем.", recordId);
            db.Entry(job).State = EntityState.Detached;
            return ExtractionRequestResult.AlreadyQueued;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            logger.LogWarning(ex, "Не удалось поставить распознавание мед-записи {RecordId} в очередь.", recordId);
            db.Entry(job).State = EntityState.Detached;
            throw;
        }

        logger.LogInformation("Распознавание мед-записи {RecordId} поставлено в очередь{Waiting}.", recordId, aiAvailable ? "" : " (ждёт ИИ)");
        return aiAvailable ? ExtractionRequestResult.Success : ExtractionRequestResult.QueuedWaitingForAi;
    }

    /// <summary>Снимок лимитов + текущего расхода для формы (батч-загрузка и обычная форма
    /// создания читают это перед стартом, тот же приём, что GET /api/attachments/limits — форма
    /// предвалидирует и подписывает «осталось N из M», не ловит 429 вслепую). Числа читаются теми
    /// же двумя запросами, что и в RequestAsync — гонка между чтением здесь и вставкой там
    /// возможна (как и у любого мягкого лимита, см. class doc RequestAsync), это осознанно.</summary>
    public async Task<ExtractionLimitsDto> GetLimitsAsync(Guid userId, CancellationToken ct = default)
    {
        var activeJobsCount = await db.MedicalDocumentExtractionJobs.AsNoTracking()
            .CountAsync(j => j.RequestedByUserId == userId &&
                (j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running), ct);

        var todayStartUtc = DateTime.UtcNow.Date;
        var jobsToday = await db.MedicalDocumentExtractionJobs.AsNoTracking()
            .CountAsync(j => j.RequestedByUserId == userId && j.CreatedAt >= todayStartUtc, ct);

        return new ExtractionLimitsDto(
            limits.Value.MaxBatchDocuments, limits.Value.MaxActiveJobsPerUser, activeJobsCount,
            limits.Value.DailyJobsPerUser, jobsToday, todayStartUtc.AddDays(1));
    }
}

/// <summary>ResetsAt — начало следующих суток UTC, когда DailyQuota обнуляется (считается по
/// MedicalDocumentExtractionJob.CreatedAt, не хранится отдельным счётчиком — см. class doc
/// ExtractionRequestService.GetLimitsAsync).</summary>
public record ExtractionLimitsDto(
    int MaxBatchDocuments, int MaxActiveJobs, int ActiveNow, int DailyQuota, int UsedToday, DateTime ResetsAt);
