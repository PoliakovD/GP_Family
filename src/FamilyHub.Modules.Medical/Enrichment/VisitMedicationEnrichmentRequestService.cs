using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Enrichment;

/// <summary>
/// Точка входа обогащения kb.global_medications_kb для препарата, упомянутого в заключении врача
/// (UX-редизайн) — вызывается из MedicalDocumentExtractionProcessor.ProcessVisitAsync при промахе
/// KbLookupService по назначенному лекарству. Зеркало LabAnalyteEnrichmentRequestService: только
/// RequestedByUserId, без FamilyId и без уведомлений (см. VisitMedicationEnrichmentJob — у визита
/// к врачу нет семейного контекста, в отличие от аптечки).
/// </summary>
public class VisitMedicationEnrichmentRequestService(
    AppDbContext db, IBackgroundJobClient backgroundJobs, ILogger<VisitMedicationEnrichmentRequestService> logger)
{
    public async Task RequestAsync(
        string normalizedName, string sourceDisplayName, Guid? medicalRecordId, Guid requestedByUserId, CancellationToken ct = default)
    {
        // Мягкая защита от дублирования внешнего запроса с семейным конвейером аптечки — та же
        // запись справочника могла уже обогащаться из MedicationEnrichmentJobs параллельно
        // (например, кто-то добавил тот же препарат в аптечку прямо сейчас). Дедуп внутри своей
        // таблицы (уникальный индекс ниже) полностью не заменяет — если гонка всё же произойдёт,
        // это лишний, но не некорректный внешний запрос (KbWriter upsert идемпотентен).
        var alreadyRunning = await db.MedicationEnrichmentJobs.AnyAsync(
            j => j.NormalizedName == normalizedName && (j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running), ct);
        if (alreadyRunning)
        {
            logger.LogDebug(
                "Обогащение «{NormalizedName}» уже идёт через конвейер аптечки, отдельная задача не создаётся", normalizedName);
            return;
        }

        var job = new VisitMedicationEnrichmentJob
        {
            Id = Guid.NewGuid(),
            NormalizedName = normalizedName,
            SourceDisplayName = sourceDisplayName,
            MedicalRecordId = medicalRecordId,
            RequestedByUserId = requestedByUserId,
            Status = EnrichmentJobStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };
        db.VisitMedicationEnrichmentJobs.Add(job);

        // Та же гарантия, что и у остальных Request-сервисов: Pending-строка и Hangfire-энкью —
        // единая единица отката, сбой постановки задачи не должен ронять основной конвейер
        // извлечения заключения врача.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
            backgroundJobs.Enqueue<VisitMedicationEnrichmentProcessor>(p => p.RunAsync(job.Id, CancellationToken.None));
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            await tx.RollbackAsync(ct);
            logger.LogDebug(ex, "Обогащение препарата «{NormalizedName}» уже в очереди, пропускаем", normalizedName);
            db.Entry(job).State = EntityState.Detached;
            return;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            logger.LogWarning(ex, "Не удалось поставить обогащение препарата «{NormalizedName}» в очередь", normalizedName);
            db.Entry(job).State = EntityState.Detached;
            return;
        }

        logger.LogInformation(
            "Обогащение справочника медикаментов (из заключения врача) поставлено в очередь: «{Name}» ({NormalizedName})",
            sourceDisplayName, normalizedName);
    }
}
