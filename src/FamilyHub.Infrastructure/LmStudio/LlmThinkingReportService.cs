using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Infrastructure.LmStudio;

/// <summary>
/// Пишет живой обрывок "мысли" модели (CurrentThought) на нужную из четырёх таблиц задач — план
/// "живой поток мыслей". Throttled: ReportAsync молча игнорирует вызовы чаще чем раз в
/// ThrottleInterval — SSE-чанков за один ответ модели могут быть десятки-сотни, писать в БД на
/// каждый было бы избыточно. Троттлинг — простое поле экземпляра, не статический словарь по
/// (kind, jobId): сервис scoped, живёт ровно на одну задачу/HTTP-запрос, а LmStudioConcurrencyGate
/// в любом случае гарантирует не больше одного активного стрима во всём процессе одновременно.
///
/// ExecuteUpdateAsync (не через SaveChangesAsync) — совместимо с тем, что тот же scoped
/// AppDbContext параллельно держит ДРУГИЕ отслеживаемые сущности (сам job, MedicalRecord и т.п.) у
/// вызывающего процессора: CurrentThought никогда не читается/не пишется через трекер, поэтому
/// нет риска, что последующий SaveChangesAsync процессора перезапишет эту колонку устаревшим
/// закэшированным значением (тот же приём, что уже используется в этом кодовой базе для
/// MedicalRecord.ExtractionStatus, см. MedicalDocumentExtractionProcessor.FailAsync).
/// </summary>
public class LlmThinkingReportService(AppDbContext db, ILogger<LlmThinkingReportService> logger)
{
    private static readonly TimeSpan ThrottleInterval = TimeSpan.FromMilliseconds(700);

    private DateTime _lastWriteAt = DateTime.MinValue;

    /// <summary>Throttled — тихо не делает ничего, если предыдущая запись была недавно.</summary>
    public async Task ReportAsync(LlmJobKind kind, Guid jobId, string text, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now - _lastWriteAt < ThrottleInterval) return;
        _lastWriteAt = now;
        await WriteAsync(kind, jobId, text, ct);
    }

    /// <summary>Не throttled — вызывается ровно один раз в конце потокового вызова (успех, ошибка
    /// или &lt;think&gt; закрылся раньше конца ответа), чтобы не оставлять "зависшую" мысль от
    /// прошлого вызова на экране после того, как модель перешла к следующему шагу.</summary>
    public Task ClearAsync(LlmJobKind kind, Guid jobId, CancellationToken ct) => WriteAsync(kind, jobId, null, ct);

    private async Task WriteAsync(LlmJobKind kind, Guid jobId, string? text, CancellationToken ct)
    {
        try
        {
            var rows = kind switch
            {
                LlmJobKind.Extraction => await db.MedicalDocumentExtractionJobs
                    .Where(j => j.Id == jobId)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.CurrentThought, text), ct),
                LlmJobKind.LabAnalyteEnrichment => await db.LabAnalyteEnrichmentJobs
                    .Where(j => j.Id == jobId)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.CurrentThought, text), ct),
                LlmJobKind.MedicationEnrichment => await db.MedicationEnrichmentJobs
                    .Where(j => j.Id == jobId)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.CurrentThought, text), ct),
                LlmJobKind.VisitMedicationEnrichment => await db.VisitMedicationEnrichmentJobs
                    .Where(j => j.Id == jobId)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.CurrentThought, text), ct),
                _ => 0,
            };
            _ = rows; // 0 — задача уже не существует/id устарел, не ошибка (см. catch ниже — не для этого случая).
        }
        catch (Exception ex)
        {
            // "Текущая мысль" — чисто UI-удобство поверх основного конвейера; сбой записи не
            // должен ронять сам вызов LM Studio (тот же принцип, что и у TrySendAsync в
            // NotificationSendingService — второстепенный побочный эффект логируется и молча
            // пропускается).
            logger.LogDebug(ex, "Не удалось записать текущую мысль для {Kind} {JobId}", kind, jobId);
        }
    }
}
