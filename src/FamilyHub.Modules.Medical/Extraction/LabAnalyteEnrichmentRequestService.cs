using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Точка входа конвейера обогащения справочника показателей (ветка medicalrecords) — вызывается
/// из MedicalDocumentExtractionProcessor на этапе Linking при промахе поиска в kb.global_lab_analytes_kb.
/// Зеркало EnrichmentRequestService.EnqueueAsync (этап 4): дедуп на уровне БД (частичный уникальный
/// индекс по (NormalizedName, Specimen) среди Pending/Running, см. LabAnalyteEnrichmentJobConfiguration) —
/// второй анализ с тем же непризнанным показателем И тем же биоматериалом молча становится no-op,
/// а не вторым внешним запросом; другой биоматериал того же показателя — отдельная задача.
/// </summary>
public class LabAnalyteEnrichmentRequestService(
    AppDbContext db, IBackgroundJobClient backgroundJobs, ILogger<LabAnalyteEnrichmentRequestService> logger)
{
    public async Task RequestAsync(
        string normalizedName, SpecimenType specimen, string sourceDisplayName, Guid? labIndicatorId,
        Guid requestedByUserId, CancellationToken ct = default) =>
        await RequestAsync(normalizedName, specimen, sourceDisplayName, labIndicatorId, requestedByUserId, force: false, ct);

    /// <summary>force=true — переобогащение уже существующей KB-записи (см. LabAnalyteKbReenrichJob),
    /// а не первичное обогащение промаха. Дедуп на уровне БД тот же — если для этой пары уже есть
    /// Pending/Running-задача (в т.ч. форсированная другим прогоном reenrich), вторая молча
    /// становится no-op, как и обычно.</summary>
    public async Task RequestAsync(
        string normalizedName, SpecimenType specimen, string sourceDisplayName, Guid? labIndicatorId,
        Guid requestedByUserId, bool force, CancellationToken ct = default)
    {
        var job = new LabAnalyteEnrichmentJob
        {
            Id = Guid.NewGuid(),
            NormalizedName = normalizedName,
            Specimen = specimen,
            SourceDisplayName = sourceDisplayName,
            LabIndicatorId = labIndicatorId,
            RequestedByUserId = requestedByUserId,
            Force = force,
            Status = EnrichmentJobStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };
        db.LabAnalyteEnrichmentJobs.Add(job);

        // Та же гарантия, что и у EnrichmentRequestService: Pending-строка и Hangfire-энкью —
        // единая единица отката, сбой постановки задачи (включая недоступность Hangfire) не
        // должен ронять основной конвейер извлечения показателей.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
            backgroundJobs.Enqueue<LabAnalyteEnrichmentProcessor>(p => p.RunAsync(job.Id, CancellationToken.None));
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            await tx.RollbackAsync(ct);
            logger.LogDebug(ex, "Обогащение показателя «{NormalizedName}» уже в очереди, пропускаем", normalizedName);
            db.Entry(job).State = EntityState.Detached;
            return;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            logger.LogWarning(ex, "Не удалось поставить обогащение показателя «{NormalizedName}» в очередь", normalizedName);
            db.Entry(job).State = EntityState.Detached;
            return;
        }

        logger.LogInformation(
            "Обогащение справочника показателей поставлено в очередь: «{Name}» ({NormalizedName})", sourceDisplayName, normalizedName);
    }
}
