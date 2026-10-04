using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Kb;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Повторная сверка со справочником задач обогащения показателей, запаркованных в «Одобрении» на платный поиск
/// (<see cref="EnrichmentJobStatus.AwaitingSearchApproval"/>). Процессор сверяется со справочником один раз — в момент
/// парковки; статья, появившаяся или ставшая находимой ПОЗЖЕ (одобрен результат соседней задачи, админ поправил
/// синонимы/биоматериал, <see cref="LabAnalyteKbRekeyJob"/> перекейил строку со старым ключом), задачу из очереди не
/// убирала — админа просили оплатить поиск того, что в справочнике уже есть. Попадание закрывает задачу ровно как
/// ветка «уже есть в справочнике» LabAnalyteEnrichmentProcessor.RunAsync: Completed + KbId + дозаполнение показателей.
///
/// Force-задачи не трогаются: их цель — пройти конвейер поверх уже существующей статьи (переобогащение, нет нормы
/// в единицах бланка), попадание в справочник для них ожидаемо и не повод закрывать.
///
/// Ключ задачи перед поиском приводится к текущей форме (<see cref="LabAnalyteNormalizer.RenormalizeKey"/>) —
/// задача, запаркованная до смены нормализатора, несёт старый ключ, а справочник после перекейовки — уже новый.
/// Закрытие — условным UPDATE по статусу: параллельный клик «Одобрить»/«Отклонить» не перетирается.
/// </summary>
[Queue("enrichment")]
public class LabAnalyteParkedKbResolver(
    AppDbContext db,
    LabAnalyteKbLookupService kbLookup,
    IBackgroundJobClient backgroundJobs,
    ILogger<LabAnalyteParkedKbResolver> logger)
{
    /// <returns>Сколько задач закрыто попаданием в справочник.</returns>
    public async Task<int> ResolveAsync(CancellationToken ct = default)
    {
        var parked = await db.LabAnalyteEnrichmentJobs.AsNoTracking()
            .Where(j => j.Status == EnrichmentJobStatus.AwaitingSearchApproval && !j.Force)
            .Select(j => new { j.Id, j.NormalizedName, j.SpecimenKbId, j.SourceDisplayName })
            .ToListAsync(ct);

        var resolved = 0;
        foreach (var job in parked)
        {
            var key = LabAnalyteNormalizer.RenormalizeKey(job.NormalizedName);
            if (key.Length == 0) key = job.NormalizedName;

            var existing = await kbLookup.LookupAsync(key, job.SpecimenKbId, ct);
            if (existing.Kind != KbLookupKind.Hit) continue;

            var now = DateTime.UtcNow;
            var updated = await db.LabAnalyteEnrichmentJobs
                .Where(j => j.Id == job.Id && j.Status == EnrichmentJobStatus.AwaitingSearchApproval)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, EnrichmentJobStatus.Completed)
                    .SetProperty(j => j.KbId, existing.KbId)
                    .SetProperty(j => j.CompletedAt, now), ct);
            if (updated == 0) continue;

            resolved++;
            logger.LogInformation(
                "LabAnalyteEnrichmentJob {JobId}: «{Name}» нашёлся в справочнике («{KbName}») — платный поиск больше не нужен, задача закрыта.",
                job.Id, job.SourceDisplayName, existing.DisplayName);
            backgroundJobs.Enqueue<RecalculateIndicatorFlagsJob>(j => j.RunAsync(existing.KbId!.Value, CancellationToken.None));
        }

        return resolved;
    }
}
