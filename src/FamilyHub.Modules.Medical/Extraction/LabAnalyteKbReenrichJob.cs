using FamilyHub.Infrastructure.Persistence;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Принудительное переобогащение справочника показателей после пересборки enrich-пайплайна —
/// перегоняет существующие записи kb.global_lab_analytes_kb со старой схемой (PayloadVersion &lt;
/// LabAnalyteSummarySchema.CurrentVersion) через обычный конвейер (LabAnalyteEnrichmentProcessor),
/// переиспользуя его целиком: кэш сниппетов (LabAnalyteSearchCacheService — платный запрос за одно
/// название/биоматериал платится не больше раза за MinRefreshIntervalMonths), merge по приоритету
/// источника, дозаполнение флагов задним числом (RecalculateIndicatorFlagsJob). Force=true у
/// поставленных задач — иначе процессор увидел бы "уже есть в справочнике" и молча завершил бы
/// задачу, ничего не переобогатив (см. LabAnalyteEnrichmentProcessor.RunAsync).
///
/// Батч за один прогон, НЕ ради квоты (её больше нет) — чтобы не забить очередь "enrichment" разом
/// и не заблокировать обогащение по свежим документам живых пользователей: локальная LLM
/// обрабатывает задачи последовательно за single-flight гейтом (LmStudioConcurrencyGate). Первый
/// прогон ставится автоматически один раз после миграции на v4; повторные — через
/// POST /api/admin/kb/lab-analytes/reenrich, пока PayloadVersion не дойдёт до CurrentVersion у всех
/// строк (проверить: строк с PayloadVersion &lt; CurrentVersion больше не осталось).
/// </summary>
[Queue("enrichment")]
public class LabAnalyteKbReenrichJob(
    AppDbContext db, LabAnalyteEnrichmentRequestService enrichmentRequest, ILogger<LabAnalyteKbReenrichJob> logger)
{
    public const int BatchSize = 25;

    /// <summary>Задача поставлена системой, не конкретным пользователем — тот же приём, что
    /// FamilyId=Guid.Empty у персональных ресурсов в этом проекте.</summary>
    private static readonly Guid SystemUserId = Guid.Empty;

    public async Task RunAsync(CancellationToken ct = default)
    {
        var stale = await db.GlobalLabAnalytesKb.AsNoTracking()
            .Where(k => k.PayloadVersion < LabAnalyteSummarySchema.CurrentVersion)
            .OrderBy(k => k.UpdatedAt)
            .Take(BatchSize)
            .Select(k => new { k.NormalizedName, k.SpecimenKbId, k.DisplayName })
            .ToListAsync(ct);

        if (stale.Count == 0)
        {
            logger.LogInformation(
                "LabAnalyteKbReenrichJob: справочник полностью на текущей схеме (v{Version}) — переобогащать нечего.",
                LabAnalyteSummarySchema.CurrentVersion);
            return;
        }

        foreach (var row in stale)
        {
            await enrichmentRequest.RequestAsync(
                row.NormalizedName, row.SpecimenKbId, row.DisplayName, labIndicatorId: null, SystemUserId, force: true, ct);
        }

        logger.LogInformation(
            "LabAnalyteKbReenrichJob: поставлено {Count} задач переобогащения (схема < v{Version}). " +
            "Если это был полный батч ({BatchSize}) — вероятно, остались ещё строки, запустите ещё раз.",
            stale.Count, LabAnalyteSummarySchema.CurrentVersion, BatchSize);
    }
}
