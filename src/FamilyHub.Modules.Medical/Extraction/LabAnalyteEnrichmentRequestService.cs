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
/// индекс по (NormalizedName, SpecimenKbId) среди Pending/Running, см. LabAnalyteEnrichmentJobConfiguration) —
/// второй анализ с тем же непризнанным показателем И тем же источником молча становится no-op,
/// а не вторым внешним запросом; другой источник того же показателя — отдельная задача.
///
/// ЕДИНСТВЕННАЯ точка входа в конвейер (пересборка enrich-пайплайна) — здесь стоит жёсткий гейт:
/// источник, не подтверждённый SpecimenResolver выше порога уверенности (SpecimenKbId ==
/// SpecimenContextIds.Unresolved), никогда не ставится в очередь внешнего поиска. Это гарантирует
/// требование "не создавать справочник по неопределённому источнику" на уровне одной проверки, а
/// не на каждом вызывающем месте по отдельности.
/// </summary>
public class LabAnalyteEnrichmentRequestService(
    AppDbContext db, IBackgroundJobClient backgroundJobs, ILogger<LabAnalyteEnrichmentRequestService> logger)
{
    public async Task RequestAsync(
        string normalizedName, Guid specimenKbId, string sourceDisplayName, Guid? labIndicatorId,
        Guid requestedByUserId, CancellationToken ct = default) =>
        await RequestAsync(normalizedName, specimenKbId, sourceDisplayName, labIndicatorId, requestedByUserId, force: false, ct);

    /// <summary>force=true — переобогащение уже существующей KB-записи (см. LabAnalyteKbReenrichJob),
    /// а не первичное обогащение промаха. Дедуп на уровне БД тот же — если для этой пары уже есть
    /// Pending/Running-задача (в т.ч. форсированная другим прогоном reenrich), вторая молча
    /// становится no-op, как и обычно.</summary>
    public async Task RequestAsync(
        string normalizedName, Guid specimenKbId, string sourceDisplayName, Guid? labIndicatorId,
        Guid requestedByUserId, bool force, CancellationToken ct = default)
    {
        // Жёсткий гейт (см. class doc) — источник не резолвлен/не уверен, во внешний поиск и в
        // справочник ничего не уходит. Тихий выход, не исключение: вызывающий код (Linking-этап
        // экстракции) не должен ронять основной конвейер из-за этого.
        if (specimenKbId == SpecimenContextIds.Unresolved)
        {
            logger.LogInformation(
                "Обогащение показателя «{Name}» ({NormalizedName}) пропущено — источник не определён.",
                sourceDisplayName, normalizedName);
            return;
        }

        var job = new LabAnalyteEnrichmentJob
        {
            Id = Guid.NewGuid(),
            NormalizedName = normalizedName,
            SpecimenKbId = specimenKbId,
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
