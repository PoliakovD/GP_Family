using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Kb;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Enrichment;

/// <summary>
/// Точка входа конвейера обогащения справочника (этап 4) — вызывается сразу после сохранения
/// медикамента (см. MedicationService.CreateAsync/UpdateAsync). Нормализует имя, проверяет
/// справочник, и ТОЛЬКО при промахе/неуверенном совпадении ставит задачу в очередь Hangfire.
/// Дедуп на уровне БД (частичный уникальный индекс по NormalizedName среди Pending/Running задач,
/// см. MedicationEnrichmentJobConfiguration) — конкурентное сохранение того же препарата в другой
/// семье молча становится no-op, а не ошибкой и не вторым внешним запросом.
/// Платная квота/кулдаун здесь намеренно НЕ проверяются — этим занимается сам
/// MedicationEnrichmentProcessor (у него есть настоящий кэш сниппетов: если по названию уже
/// есть закэшированный поиск, задача выполнится мгновенно и бесплатно, повторно ходить к
/// платному API незачем). Проверка здесь заранее только дублировала бы эту логику.
/// </summary>
public class EnrichmentRequestService(
    AppDbContext db,
    KbLookupService kbLookup,
    IBackgroundJobClient backgroundJobs,
    ILogger<EnrichmentRequestService> logger) : IEnrichmentRequestService
{
    public async Task RequestAsync(Medication medication, Guid userId, CancellationToken ct = default)
    {
        var normalizedName = MedicationNameNormalizer.Normalize(medication.Name);
        if (normalizedName.Length == 0) return;

        // Candidate (неуверенное нечёткое совпадение) НЕ считается «уже есть знание» — точный
        // ключ NormalizedName у ЭТОГО названия по-прежнему не разрешается напрямую (см. KbLookupService),
        // поэтому конвейер всё равно запускается. Только Hit останавливает конвейер.
        var lookup = await kbLookup.LookupAsync(normalizedName, ct);
        if (lookup.Kind == KbLookupKind.Hit) return;

        await EnqueueAsync(medication, normalizedName, userId, ct);
    }

    public async Task<EnrichmentRefreshOutcome> RequestRefreshAsync(Medication medication, Guid userId, CancellationToken ct = default)
    {
        var normalizedName = MedicationNameNormalizer.Normalize(medication.Name);
        if (normalizedName.Length == 0) return EnrichmentRefreshOutcome.NothingToRefresh();

        // Ручной запрос («Уточнить в справочнике», GET/POST /api/medications/{id}/kb/refresh) —
        // в отличие от RequestAsync намеренно НЕ прерывается на Hit: пользователь мог заметить
        // устаревшую/неполную карточку и хочет принудительного повторного обогащения. Дедуп на
        // Pending/Running всё равно защищает от повторной постановки, пока предыдущая не завершилась.
        await EnqueueAsync(medication, normalizedName, userId, ct);
        return EnrichmentRefreshOutcome.Requested();
    }

    private async Task EnqueueAsync(Medication medication, string normalizedName, Guid userId, CancellationToken ct)
    {
        var job = new MedicationEnrichmentJob
        {
            Id = Guid.NewGuid(),
            NormalizedName = normalizedName,
            SourceDisplayName = medication.Name,
            MedicationId = medication.Id,
            RequestedByUserId = userId,
            FamilyId = medication.FamilyId,
            Status = EnrichmentJobStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };
        db.MedicationEnrichmentJobs.Add(job);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Уже есть Pending/Running задача на этот NormalizedName (частичный уникальный индекс,
            // тот же приём, что NotificationSendingService.AddIfNewAsync с DedupKey) — no-op.
            logger.LogDebug(ex, "Обогащение «{NormalizedName}» уже в очереди, пропускаем", normalizedName);
            db.Entry(job).State = EntityState.Detached;
            return;
        }

        backgroundJobs.Enqueue<MedicationEnrichmentProcessor>(p => p.RunAsync(job.Id, CancellationToken.None));
        logger.LogInformation(
            "Обогащение справочника поставлено в очередь: «{Name}» ({NormalizedName})", medication.Name, normalizedName);
    }
}
